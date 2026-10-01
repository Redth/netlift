using System.Collections.Concurrent;
using NetLift;
using NetLift.Negotiation;

namespace NetLift.ManagedHost;

internal enum ManagedAppEndpointAuthorityStatus
{
    Success = 0,
    InvalidArgument = 1,
    Busy = 2,
    StaleHandle = 3,
    InvalidState = 4,
    NegotiationRejected = 5,
    ManagedFailure = 6,
    UnsupportedVersion = 7,
    BufferTooSmall = 8
}

internal sealed class ManagedAppEndpointAuthority
{
    private const int MaximumManifestBytes = 1024 * 1024;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<ulong, EndpointState> _endpoints = new();
    private long _nextEndpointToken;
    private ulong _activeEndpointToken;
    private bool _moduleIdentityConsumed;

    private ManagedAppEndpointAuthority()
    {
    }

    internal static ManagedAppEndpointAuthority Process { get; } = new();

    internal ManagedAppEndpointBindingProvider BindingProvider { get; } = new();

    internal int EndpointCount => _endpoints.Count;

    internal Action? BeforeStartForTesting { get; set; }

    internal Action? BeforePublishForTesting { get; set; }

    internal void MarkModuleIdentityConsumed()
    {
        lock (_gate)
            _moduleIdentityConsumed = true;
    }

    internal ManagedAppEndpointAuthorityStatus CreateEndpoint(
        ReadOnlySpan<byte> trustedLocalManifest,
        bool localCompatibleStartOptIn,
        ReadOnlySpan<byte> trustedPeerSchemaHash,
        uint minimumPeerContractMajor,
        uint minimumPeerContractMinor,
        out ulong endpointToken)
    {
        endpointToken = 0;
        if (trustedLocalManifest.IsEmpty ||
            trustedLocalManifest.Length > MaximumManifestBytes ||
            trustedPeerSchemaHash.Length != 32 ||
            minimumPeerContractMajor == 0)
            return ManagedAppEndpointAuthorityStatus.InvalidArgument;

        byte[] localSnapshot;
        NegotiationManifest localManifest;
        try
        {
            localSnapshot = trustedLocalManifest.ToArray();
            localManifest = NegotiationManifest.LoadEmbedded(localSnapshot);
        }
        catch (NegotiationException)
        {
            return ManagedAppEndpointAuthorityStatus.InvalidArgument;
        }
        catch (ArgumentException)
        {
            return ManagedAppEndpointAuthorityStatus.InvalidArgument;
        }

        if (!localManifest.IsAsyncCallbackProfile)
            return ManagedAppEndpointAuthorityStatus.InvalidArgument;

        lock (_gate)
        {
            if (_moduleIdentityConsumed)
                return ManagedAppEndpointAuthorityStatus.InvalidState;
            if (_activeEndpointToken != 0)
                return ManagedAppEndpointAuthorityStatus.Busy;
            if (_nextEndpointToken == long.MaxValue)
                return ManagedAppEndpointAuthorityStatus.ManagedFailure;

            var token = checked((ulong)++_nextEndpointToken);
            var state = new EndpointState(
                new CompatibleStartReference(
                    localSnapshot,
                    localCompatibleStartOptIn,
                    minimumPeerContractMajor,
                    minimumPeerContractMinor),
                localSnapshot,
                Convert.ToHexString(trustedPeerSchemaHash).ToLowerInvariant(),
                minimumPeerContractMajor,
                minimumPeerContractMinor);
            if (!_endpoints.TryAdd(token, state))
                return ManagedAppEndpointAuthorityStatus.ManagedFailure;

            _activeEndpointToken = token;
            endpointToken = token;
            return ManagedAppEndpointAuthorityStatus.Success;
        }
    }

    internal ManagedAppEndpointAuthorityStatus StartEndpoint(
        ulong endpointToken,
        ReadOnlySpan<byte> peerManifest,
        ReadOnlySpan<byte> peerHello,
        bool peerCompatibleStartOptIn,
        out LiftAsyncCallbackV1HostDescriptor? descriptor,
        out NegotiationErrorCode? errorCode)
    {
        descriptor = null;
        errorCode = null;
        if (endpointToken == 0)
            return ManagedAppEndpointAuthorityStatus.InvalidArgument;
        if (!_endpoints.TryGetValue(endpointToken, out var state))
            return ManagedAppEndpointAuthorityStatus.StaleHandle;

        CompatibleStartReference reference;
        lock (state.Gate)
        {
            if (state.Lifecycle is
                EndpointLifecycle.Closing or
                EndpointLifecycle.ClosePending or
                EndpointLifecycle.Closed)
                return ManagedAppEndpointAuthorityStatus.StaleHandle;
            if (state.Lifecycle == EndpointLifecycle.Starting)
                return ManagedAppEndpointAuthorityStatus.Busy;
            if (state.Lifecycle != EndpointLifecycle.Created)
            {
                errorCode = NegotiationErrorCode.OptedOut;
                return ManagedAppEndpointAuthorityStatus.NegotiationRejected;
            }

            if (state.Reference is not { } currentReference)
                return ManagedAppEndpointAuthorityStatus.StaleHandle;
            reference = currentReference;
            state.Lifecycle = EndpointLifecycle.Starting;
        }

        CompatibleStartResult result;
        try
        {
            BeforeStartForTesting?.Invoke();
            result = reference.Start(
                peerManifest,
                peerHello,
                new PeerManifestTrust(
                    state.TrustedPeerSchemaHash,
                    state.MinimumPeerContractMajor,
                    state.MinimumPeerContractMinor),
                peerCompatibleStartOptIn);
        }
        catch (Exception exception)
        {
            return FailStart(state, exception);
        }

        if (!result.Succeeded)
        {
            lock (state.Gate)
            {
                state.Lifecycle = EndpointLifecycle.StartFailed;
                errorCode = result.ErrorCode;
            }
            return ManagedAppEndpointAuthorityStatus.NegotiationRejected;
        }

        LiftAsyncCallbackV1HostDescriptor candidate;
        LiftAsyncCallbackV1HostDescriptor stateDescriptor;
        LiftAsyncCallbackV1HostDescriptor returnedDescriptor;
        try
        {
            candidate = ManagedAppEndpointSchemaFactory.CreateDescriptor(
                endpointToken,
                state.LocalManifest,
                result.Contract!);
            BeforePublishForTesting?.Invoke();
            stateDescriptor = LiftAsyncCallbackV1Wire.CopyDescriptor(candidate);
            returnedDescriptor = LiftAsyncCallbackV1Wire.CopyDescriptor(candidate);
        }
        catch (Exception exception)
        {
            return FailStart(state, exception);
        }

        try
        {
            lock (state.Gate)
            {
                if (state.Lifecycle != EndpointLifecycle.Starting)
                    return ManagedAppEndpointAuthorityStatus.StaleHandle;

                var publishStatus = BindingProvider.Publish(candidate);
                if (publishStatus != ManagedAppEndpointBindingStatus.Success)
                {
                    state.Lifecycle = EndpointLifecycle.StartFailed;
                    return ManagedAppEndpointAuthorityStatus.ManagedFailure;
                }

                state.Descriptor = stateDescriptor;
                state.Lifecycle = EndpointLifecycle.Started;
                descriptor = returnedDescriptor;
                return ManagedAppEndpointAuthorityStatus.Success;
            }
        }
        catch (Exception exception)
        {
            return FailStart(state, exception);
        }
    }

    private static ManagedAppEndpointAuthorityStatus FailStart(
        EndpointState state,
        Exception exception)
    {
        Console.Error.WriteLine(
            $"ManagedAppEndpointAuthority.StartEndpoint failed ({exception.GetType().Name}): {exception.Message}");
        lock (state.Gate)
        {
            if (state.Lifecycle == EndpointLifecycle.Starting)
                state.Lifecycle = EndpointLifecycle.StartFailed;
        }
        return ManagedAppEndpointAuthorityStatus.ManagedFailure;
    }

    internal ManagedAppEndpointAuthorityStatus GetSnapshot(
        ulong endpointToken,
        ManagedAppEndpointAuthoritySnapshotKind kind,
        out byte[]? snapshot)
    {
        snapshot = null;
        if (endpointToken == 0)
            return ManagedAppEndpointAuthorityStatus.InvalidArgument;
        if (!_endpoints.TryGetValue(endpointToken, out var state))
            return ManagedAppEndpointAuthorityStatus.StaleHandle;

        lock (state.Gate)
        {
            if (state.Lifecycle is
                EndpointLifecycle.Closing or
                EndpointLifecycle.ClosePending or
                EndpointLifecycle.Closed)
                return ManagedAppEndpointAuthorityStatus.StaleHandle;
            if (state.Lifecycle == EndpointLifecycle.Starting)
                return ManagedAppEndpointAuthorityStatus.Busy;
            if (state.Lifecycle != EndpointLifecycle.Started ||
                state.Descriptor is not { } descriptor)
                return ManagedAppEndpointAuthorityStatus.InvalidState;

            snapshot = kind switch
            {
                ManagedAppEndpointAuthoritySnapshotKind.LocalCanonicalManifest =>
                    state.LocalManifest.ToArray(),
                ManagedAppEndpointAuthoritySnapshotKind.PeerRequestSchema =>
                    descriptor.PeerRequestSchema.ToArray(),
                ManagedAppEndpointAuthoritySnapshotKind.ManagedResultSchema =>
                    descriptor.ManagedResultSchema.ToArray(),
                _ => null
            };
            return snapshot is null
                ? ManagedAppEndpointAuthorityStatus.InvalidArgument
                : ManagedAppEndpointAuthorityStatus.Success;
        }
    }

    internal ManagedAppEndpointAuthorityStatus CloseEndpoint(ulong endpointToken)
    {
        if (endpointToken == 0)
            return ManagedAppEndpointAuthorityStatus.InvalidArgument;
        if (!_endpoints.TryGetValue(endpointToken, out var state))
            return ManagedAppEndpointAuthorityStatus.StaleHandle;

        lock (state.Gate)
        {
            if (state.Lifecycle == EndpointLifecycle.Closed)
                return ManagedAppEndpointAuthorityStatus.StaleHandle;
            if (state.Lifecycle == EndpointLifecycle.Starting)
                return ManagedAppEndpointAuthorityStatus.Busy;

            if (state.Lifecycle is EndpointLifecycle.Started or EndpointLifecycle.ClosePending)
            {
                state.Lifecycle = EndpointLifecycle.Closing;
                ManagedAppEndpointBindingStatus invalidateStatus;
                try
                {
                    invalidateStatus = BindingProvider.Invalidate(endpointToken);
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(
                        $"ManagedAppEndpointAuthority.CloseEndpoint failed ({exception.GetType().Name}): {exception.Message}");
                    state.Lifecycle = EndpointLifecycle.ClosePending;
                    return ManagedAppEndpointAuthorityStatus.ManagedFailure;
                }

                if (invalidateStatus == ManagedAppEndpointBindingStatus.Busy)
                {
                    state.Lifecycle = EndpointLifecycle.ClosePending;
                    return ManagedAppEndpointAuthorityStatus.Busy;
                }
                if (invalidateStatus is not (
                        ManagedAppEndpointBindingStatus.Success or
                        ManagedAppEndpointBindingStatus.AlreadyInvalidated))
                {
                    state.Lifecycle = EndpointLifecycle.ClosePending;
                    return ManagedAppEndpointAuthorityStatus.ManagedFailure;
                }
            }

            if (!((ICollection<KeyValuePair<ulong, EndpointState>>)_endpoints)
                    .Remove(new KeyValuePair<ulong, EndpointState>(endpointToken, state)))
            {
                state.Lifecycle = EndpointLifecycle.ClosePending;
                Console.Error.WriteLine(
                    "ManagedAppEndpointAuthority.CloseEndpoint could not remove the exact endpoint record.");
                return ManagedAppEndpointAuthorityStatus.ManagedFailure;
            }

            state.Lifecycle = EndpointLifecycle.Closed;
            state.Reference = null;
            state.LocalManifest = [];
            state.TrustedPeerSchemaHash = string.Empty;
            state.Descriptor = null;
        }

        lock (_gate)
        {
            if (_activeEndpointToken == endpointToken)
                _activeEndpointToken = 0;
        }
        return ManagedAppEndpointAuthorityStatus.Success;
    }

    private enum EndpointLifecycle
    {
        Created,
        Starting,
        Started,
        StartFailed,
        ClosePending,
        Closing,
        Closed
    }

    private sealed class EndpointState(
        CompatibleStartReference reference,
        byte[] localManifest,
        string trustedPeerSchemaHash,
        uint minimumPeerContractMajor,
        uint minimumPeerContractMinor)
    {
        public object Gate { get; } = new();
        public CompatibleStartReference? Reference { get; set; } = reference;
        public byte[] LocalManifest { get; set; } = localManifest;
        public string TrustedPeerSchemaHash { get; set; } = trustedPeerSchemaHash;
        public uint MinimumPeerContractMajor { get; } = minimumPeerContractMajor;
        public uint MinimumPeerContractMinor { get; } = minimumPeerContractMinor;
        public LiftAsyncCallbackV1HostDescriptor? Descriptor { get; set; }
        public EndpointLifecycle Lifecycle { get; set; } = EndpointLifecycle.Created;
    }
}
