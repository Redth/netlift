using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetLift;

namespace NetLift.ManagedHost;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct ManagedAppHostApiV1
{
    public uint StructSize;
    public uint ApiVersion;
    public delegate* unmanaged[Cdecl]<
        ulong,
        byte*,
        uint,
        byte*,
        uint,
        byte*,
        uint,
        byte*,
        uint,
        byte*,
        uint,
        ulong*,
        int> RegisterGeneratedHostFromPath;
    public delegate* unmanaged[Cdecl]<ulong, int> CloseGeneratedHostLease;
}

public enum ManagedAppHostStatusV1 : int
{
    Success = 0,
    InvalidArgument = 0x4E4C0101,
    UnsupportedVersion = 0x4E4C0102,
    BufferTooSmall = 0x4E4C0103,
    EndpointUnavailable = 0x4E4C0104,
    ModuleUnavailable = 0x4E4C0105,
    SchemaMismatch = 0x4E4C0106,
    RegistrationFailure = 0x4E4C0107,
    Busy = 0x4E4C0108,
    ManagedFailure = 0x4E4C0109,
    StaleHandle = 0x4E4C010A,
    AlreadyClosed = 0x4E4C010B
}

public static unsafe class ManagedAppHostApiV1Exports
{
    public const uint Version = 1;
    public const int MaximumPathBytes = 4096;
    public const int MaximumProviderNameBytes = 1024;
    public const int MaximumNlpsBytes = 344;
    public const int MaximumNlmrBytes = 392;
    public const int MaximumManifestBytes = 1024 * 1024;

    private const int NlpsHeaderSize = 88;
    private const int NlmrHeaderSize = 136;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly ConcurrentDictionary<ulong, ManagedAppHostRegistrationLease> Leases = new();
    private static readonly ConcurrentDictionary<ulong, byte> ClosedLeases = new();
    private static IManagedAppEndpointBindingProvider? _testBindingProvider;
    private static long _nextLeaseId;

    internal static int OutstandingLeaseCount => Leases.Count;

    internal static IManagedAppEndpointBindingProvider? ConfiguredBindingProvider =>
        Volatile.Read(ref _testBindingProvider) ??
        ManagedAppEndpointAuthority.Process.BindingProvider;

    internal static void ConfigureTestEndpointBindingProvider(
        IManagedAppEndpointBindingProvider bindingProvider)
    {
        ArgumentNullException.ThrowIfNull(bindingProvider);
        if (Interlocked.CompareExchange(ref _testBindingProvider, bindingProvider, null) is not null)
            throw new InvalidOperationException(
                "The test endpoint-binding provider has already been configured.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetApiV1(
        uint requestedVersion,
        uint outputCapacity,
        uint* requiredSize,
        ManagedAppHostApiV1* output) =>
        GetApiV1Core(requestedVersion, outputCapacity, requiredSize, output);

    internal static int GetApiV1Core(
        uint requestedVersion,
        uint outputCapacity,
        uint* requiredSize,
        ManagedAppHostApiV1* output)
    {
        if (requiredSize is null)
            return (int)ManagedAppHostStatusV1.InvalidArgument;

        *requiredSize = (uint)sizeof(ManagedAppHostApiV1);
        if (requestedVersion != Version)
            return (int)ManagedAppHostStatusV1.UnsupportedVersion;
        if (output is null)
            return outputCapacity == 0
                ? (int)ManagedAppHostStatusV1.Success
                : (int)ManagedAppHostStatusV1.InvalidArgument;
        if (outputCapacity < sizeof(ManagedAppHostApiV1))
            return (int)ManagedAppHostStatusV1.BufferTooSmall;

        *output = new ManagedAppHostApiV1
        {
            StructSize = (uint)sizeof(ManagedAppHostApiV1),
            ApiVersion = Version,
            RegisterGeneratedHostFromPath = &RegisterGeneratedHostFromPath,
            CloseGeneratedHostLease = &CloseGeneratedHostLease
        };
        return (int)ManagedAppHostStatusV1.Success;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int RegisterGeneratedHostFromPath(
        ulong endpointToken,
        byte* liveNlps,
        uint liveNlpsLength,
        byte* liveNlmr,
        uint liveNlmrLength,
        byte* localCanonicalManifest,
        uint localCanonicalManifestLength,
        byte* modulePath,
        uint modulePathLength,
        byte* providerTypeName,
        uint providerTypeNameLength,
        ulong* opaqueHostLease)
    {
        if (opaqueHostLease is null)
            return (int)ManagedAppHostStatusV1.InvalidArgument;

        *opaqueHostLease = 0;
        if (endpointToken == 0 ||
            !IsValidBuffer(liveNlps, liveNlpsLength, MaximumNlpsBytes) ||
            !IsValidBuffer(liveNlmr, liveNlmrLength, MaximumNlmrBytes) ||
            !IsValidBuffer(
                localCanonicalManifest, localCanonicalManifestLength, MaximumManifestBytes) ||
            !IsValidBuffer(modulePath, modulePathLength, MaximumPathBytes) ||
            !IsValidBuffer(providerTypeName, providerTypeNameLength, MaximumProviderNameBytes))
            return (int)ManagedAppHostStatusV1.InvalidArgument;

        if (!TryDecodeStrictUtf8(modulePath, modulePathLength, out var decodedPath) ||
            !TryDecodeStrictUtf8(providerTypeName, providerTypeNameLength, out var decodedProvider) ||
            string.IsNullOrWhiteSpace(decodedPath) ||
            string.IsNullOrWhiteSpace(decodedProvider))
            return (int)ManagedAppHostStatusV1.InvalidArgument;

        var nlpsSnapshot = new ReadOnlySpan<byte>(
            liveNlps, checked((int)liveNlpsLength)).ToArray();
        var nlmrSnapshot = new ReadOnlySpan<byte>(
            liveNlmr, checked((int)liveNlmrLength)).ToArray();
        var localManifestSnapshot = new ReadOnlySpan<byte>(
            localCanonicalManifest, checked((int)localCanonicalManifestLength)).ToArray();
        if (!IsStrictUtf8(localManifestSnapshot) ||
            !IsValidProviderTypeName(decodedProvider))
            return (int)ManagedAppHostStatusV1.InvalidArgument;

        var status = RegisterGeneratedHostCore(
            endpointToken,
            nlpsSnapshot,
            nlmrSnapshot,
            localManifestSnapshot,
            decodedProvider,
            () => ManagedAppModuleLease.Load(decodedPath, decodedProvider),
            opaqueHostLease);
        return status;
    }

    internal static int RegisterGeneratedHostFromCaptured(
        ulong endpointToken,
        ReadOnlyMemory<byte> liveNlps,
        ReadOnlyMemory<byte> liveNlmr,
        ReadOnlyMemory<byte> localCanonicalManifest,
        CapturedManagedAppModule? capturedModule,
        string providerTypeName,
        out ulong opaqueHostLease)
    {
        opaqueHostLease = 0;
        if (endpointToken == 0 ||
            liveNlps.Length is 0 or > MaximumNlpsBytes ||
            liveNlmr.Length is 0 or > MaximumNlmrBytes ||
            localCanonicalManifest.Length is 0 or > MaximumManifestBytes ||
            capturedModule is null ||
            !IsValidProviderTypeName(providerTypeName))
            return (int)ManagedAppHostStatusV1.InvalidArgument;

        var nlpsSnapshot = liveNlps.ToArray();
        var nlmrSnapshot = liveNlmr.ToArray();
        var localManifestSnapshot = localCanonicalManifest.ToArray();
        if (!IsStrictUtf8(localManifestSnapshot))
            return (int)ManagedAppHostStatusV1.InvalidArgument;

        CapturedManagedAppModule capturedSnapshot;
        try
        {
            capturedSnapshot = capturedModule.Copy();
        }
        catch (ArgumentException)
        {
            return (int)ManagedAppHostStatusV1.InvalidArgument;
        }
        ulong leaseId = 0;
        var status = RegisterGeneratedHostCore(
            endpointToken,
            nlpsSnapshot,
            nlmrSnapshot,
            localManifestSnapshot,
            providerTypeName,
            () => ManagedAppModuleLease.LoadCaptured(capturedSnapshot, providerTypeName),
            &leaseId);
        opaqueHostLease = leaseId;
        return status;
    }

    private static int RegisterGeneratedHostCore(
        ulong endpointToken,
        byte[] nlpsSnapshot,
        byte[] nlmrSnapshot,
        byte[] localManifestSnapshot,
        string providerTypeName,
        Func<ManagedAppModuleLease> loadModule,
        ulong* opaqueHostLease)
    {
        *opaqueHostLease = 0;
        var bindingProvider = ConfiguredBindingProvider
            ?? throw new InvalidOperationException(
                "The source-owned endpoint authority has no binding provider.");

        ManagedAppEndpointBindingLease? binding = null;
        ManagedAppModuleLease? moduleLease = null;
        GeneratedHostRegistration? registration = null;
        var failedLoadsBefore = ManagedAppModuleLease.FailedLoadRootCount;
        try
        {
            binding = bindingProvider.Acquire(endpointToken);
            if (binding is null)
                return (int)ManagedAppHostStatusV1.StaleHandle;

            var bindingStatus = ValidateBinding(
                endpointToken,
                binding,
                nlpsSnapshot,
                nlmrSnapshot,
                localManifestSnapshot);
            if (bindingStatus != (int)ManagedAppHostStatusV1.Success)
                return DisposeBeforeLoad(
                    endpointToken, binding, bindingStatus, opaqueHostLease);

            moduleLease = loadModule();
            if (ReferenceEquals(
                    bindingProvider,
                    ManagedAppEndpointAuthority.Process.BindingProvider))
                ManagedAppEndpointAuthority.Process.MarkModuleIdentityConsumed();
            var moduleApi = moduleLease.Api;
            var moduleManifest = CopyCanonicalManifest(moduleApi);
            var moduleHash = moduleLease.SchemaHash;
            if (!ManifestMatchesSchemaHash(moduleManifest, moduleHash) ||
                !CryptographicOperations.FixedTimeEquals(
                    moduleHash, binding.Descriptor.LocalSchemaHash.Span))
            {
                return RetainWithoutRegistration(
                    moduleLease,
                    binding,
                    permanentQuarantine: false,
                    opaqueHostLease,
                    (int)ManagedAppHostStatusV1.SchemaMismatch);
            }

            if (!moduleManifest.AsSpan().SequenceEqual(localManifestSnapshot))
            {
                return RetainWithoutRegistration(
                    moduleLease,
                    binding,
                    permanentQuarantine: false,
                    opaqueHostLease,
                    (int)ManagedAppHostStatusV1.SchemaMismatch);
            }

            var registerStatus = GeneratedHostRegistration.Register(
                moduleLease,
                new DescriptorBoundHostRegistry(binding.Registry, binding.Descriptor),
                out registration);
            if (registration is not null)
            {
                var status = MapBridgeStatus(registerStatus);
                if (status == (int)ManagedAppHostStatusV1.Success &&
                    (binding.Registry is not IManagedAppEndpointHostRegistry routeRegistry ||
                     !routeRegistry.EnableRouteDispatch()))
                    status = (int)ManagedAppHostStatusV1.ManagedFailure;

                return Retain(
                    new ManagedAppHostRegistrationLease(
                        endpointToken, moduleLease, registration, binding,
                        permanentQuarantine: false),
                    opaqueHostLease,
                    status);
            }

            if (moduleLease.IsReleased)
                moduleLease = null;

            return DisposeBeforeLoad(
                endpointToken, binding, MapBridgeStatus(registerStatus), opaqueHostLease, moduleLease);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"ManagedAppHostApiV1 registration failed ({exception.GetType().Name}): {exception.Message}");
            if (ReferenceEquals(
                    bindingProvider,
                    ManagedAppEndpointAuthority.Process.BindingProvider) &&
                ManagedAppModuleLease.ModuleLoadAttempted)
                ManagedAppEndpointAuthority.Process.MarkModuleIdentityConsumed();
            var permanentQuarantine =
                ManagedAppModuleLease.FailedLoadRootCount > failedLoadsBefore;
            if (registration is not null ||
                moduleLease is { IsReleased: false } ||
                permanentQuarantine)
            {
                return Retain(
                    new ManagedAppHostRegistrationLease(
                        endpointToken,
                        moduleLease,
                        registration,
                        binding,
                        permanentQuarantine),
                    opaqueHostLease,
                    MapException(exception));
            }

            return DisposeBeforeLoad(
                endpointToken,
                binding,
                MapException(exception),
                opaqueHostLease,
                moduleLease);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CloseGeneratedHostLease(ulong opaqueHostLease)
    {
        if (opaqueHostLease == 0)
            return (int)ManagedAppHostStatusV1.InvalidArgument;
        if (ClosedLeases.ContainsKey(opaqueHostLease))
            return (int)ManagedAppHostStatusV1.AlreadyClosed;
        if (!Leases.TryGetValue(opaqueHostLease, out var lease))
            return (int)ManagedAppHostStatusV1.StaleHandle;

        var status = lease.Close();
        if (status is (int)ManagedAppHostStatusV1.Success or
            (int)ManagedAppHostStatusV1.AlreadyClosed)
        {
            ClosedLeases.TryAdd(opaqueHostLease, 0);
            Leases.TryRemove(
                new KeyValuePair<ulong, ManagedAppHostRegistrationLease>(
                    opaqueHostLease, lease));
        }
        return status;
    }

    private static int ValidateBinding(
        ulong endpointToken,
        ManagedAppEndpointBindingLease binding,
        ReadOnlySpan<byte> liveNlps,
        ReadOnlySpan<byte> liveNlmr,
        ReadOnlySpan<byte> localManifest)
    {
        var descriptor = binding.Descriptor;
        var registryDescriptor = binding.Registry.Descriptor;
        if (descriptor.EndpointToken != endpointToken ||
            !DescriptorsEqual(descriptor, registryDescriptor) ||
            !liveNlps.SequenceEqual(descriptor.PeerRequestSchema.Span) ||
            !liveNlmr.SequenceEqual(descriptor.ManagedResultSchema.Span))
            return (int)ManagedAppHostStatusV1.SchemaMismatch;

        if (!ValidateNlps(descriptor, liveNlps) ||
            !ValidateNlmr(descriptor, liveNlmr) ||
            localManifest.IsEmpty)
            return (int)ManagedAppHostStatusV1.SchemaMismatch;

        return (int)ManagedAppHostStatusV1.Success;
    }

    private static bool ValidateNlps(
        LiftAsyncCallbackV1HostDescriptor descriptor,
        ReadOnlySpan<byte> nlps) =>
        nlps.Length is >= NlpsHeaderSize and <= NlpsHeaderSize + 4 * 64 &&
        nlps[..4].SequenceEqual("NLPS"u8) &&
        BinaryPrimitives.ReadUInt16LittleEndian(nlps[4..]) == 1 &&
        BinaryPrimitives.ReadUInt16LittleEndian(nlps[6..]) == NlpsHeaderSize &&
        BinaryPrimitives.ReadUInt32LittleEndian(nlps[8..]) == nlps.Length &&
        BinaryPrimitives.ReadUInt32LittleEndian(nlps[12..]) == descriptor.AbiVersion &&
        BinaryPrimitives.ReadUInt32LittleEndian(nlps[16..]) == descriptor.SchemaVersion &&
        BinaryPrimitives.ReadUInt32LittleEndian(nlps[28..]) == descriptor.FeatureBits &&
        BinaryPrimitives.ReadUInt32LittleEndian(nlps[32..]) == descriptor.RequiredFeatureBits &&
        BinaryPrimitives.ReadUInt32LittleEndian(nlps[36..]) == descriptor.ManagedServiceId &&
        BinaryPrimitives.ReadUInt32LittleEndian(nlps[40..]) == descriptor.ManagedMethodId &&
        BinaryPrimitives.ReadUInt32LittleEndian(nlps[44..]) == descriptor.RequestDtoId &&
        BinaryPrimitives.ReadUInt32LittleEndian(nlps[48..]) == descriptor.RequestDtoVersion &&
        nlps.Length == NlpsHeaderSize + BinaryPrimitives.ReadUInt16LittleEndian(nlps[52..]) * 4 &&
        BinaryPrimitives.ReadUInt16LittleEndian(nlps[52..]) is > 0 and <= 64 &&
        BinaryPrimitives.ReadUInt16LittleEndian(nlps[54..]) == 4 &&
        descriptor.PeerSchemaHash.Length == 32 &&
        nlps.Slice(56, 32).SequenceEqual(descriptor.PeerSchemaHash.Span);

    private static bool ValidateNlmr(
        LiftAsyncCallbackV1HostDescriptor descriptor,
        ReadOnlySpan<byte> nlmr) =>
        nlmr.Length is >= NlmrHeaderSize and <= NlmrHeaderSize + 4 * 64 &&
        nlmr[..4].SequenceEqual("NLMR"u8) &&
        BinaryPrimitives.ReadUInt16LittleEndian(nlmr[4..]) == 1 &&
        BinaryPrimitives.ReadUInt16LittleEndian(nlmr[6..]) == NlmrHeaderSize &&
        BinaryPrimitives.ReadUInt32LittleEndian(nlmr[8..]) == nlmr.Length &&
        BinaryPrimitives.ReadUInt32LittleEndian(nlmr[12..]) == 1 &&
        BinaryPrimitives.ReadUInt64LittleEndian(nlmr[16..]) == descriptor.EndpointToken &&
        BinaryPrimitives.ReadUInt32LittleEndian(nlmr[24..]) == descriptor.ProfileId &&
        BinaryPrimitives.ReadUInt32LittleEndian(nlmr[28..]) == descriptor.AbiVersion &&
        BinaryPrimitives.ReadUInt32LittleEndian(nlmr[32..]) == descriptor.SchemaVersion &&
        BinaryPrimitives.ReadUInt32LittleEndian(nlmr[44..]) == descriptor.FeatureBits &&
        BinaryPrimitives.ReadUInt32LittleEndian(nlmr[48..]) == descriptor.RequiredFeatureBits &&
        BinaryPrimitives.ReadUInt32LittleEndian(nlmr[52..]) == descriptor.ManagedServiceId &&
        BinaryPrimitives.ReadUInt32LittleEndian(nlmr[56..]) == descriptor.ManagedMethodId &&
        BinaryPrimitives.ReadUInt32LittleEndian(nlmr[60..]) == descriptor.ResultDtoId &&
        BinaryPrimitives.ReadUInt32LittleEndian(nlmr[64..]) == descriptor.ResultDtoVersion &&
        nlmr.Length == NlmrHeaderSize + BinaryPrimitives.ReadUInt16LittleEndian(nlmr[68..]) * 4 &&
        BinaryPrimitives.ReadUInt16LittleEndian(nlmr[68..]) is > 0 and <= 64 &&
        BinaryPrimitives.ReadUInt16LittleEndian(nlmr[70..]) == 4 &&
        descriptor.LocalSchemaHash.Length == 32 &&
        descriptor.PeerSchemaHash.Length == 32 &&
        nlmr.Slice(72, 32).SequenceEqual(descriptor.LocalSchemaHash.Span) &&
        nlmr.Slice(104, 32).SequenceEqual(descriptor.PeerSchemaHash.Span) &&
        nlmr.Slice(104, 32).SequenceEqual(
            descriptor.PeerRequestSchema.Span.Slice(56, 32));

    private static bool DescriptorsEqual(
        LiftAsyncCallbackV1HostDescriptor left,
        LiftAsyncCallbackV1HostDescriptor right) =>
        left.ProfileId == right.ProfileId &&
        left.AbiVersion == right.AbiVersion &&
        left.SchemaVersion == right.SchemaVersion &&
        left.FeatureBits == right.FeatureBits &&
        left.RequiredFeatureBits == right.RequiredFeatureBits &&
        left.ManagedServiceId == right.ManagedServiceId &&
        left.ManagedMethodId == right.ManagedMethodId &&
        left.RequestDtoId == right.RequestDtoId &&
        left.RequestDtoVersion == right.RequestDtoVersion &&
        left.ResultDtoId == right.ResultDtoId &&
        left.ResultDtoVersion == right.ResultDtoVersion &&
        left.NativeServiceId == right.NativeServiceId &&
        left.NativeMethodId == right.NativeMethodId &&
        left.EndpointToken == right.EndpointToken &&
        left.LocalSchemaHash.Span.SequenceEqual(right.LocalSchemaHash.Span) &&
        left.PeerSchemaHash.Span.SequenceEqual(right.PeerSchemaHash.Span) &&
        left.PeerRequestSchema.Span.SequenceEqual(right.PeerRequestSchema.Span) &&
        left.ManagedResultSchema.Span.SequenceEqual(right.ManagedResultSchema.Span);

    private static byte[] CopyCanonicalManifest(
        LiftAsyncCallbackV1GeneratedHostModuleApiV2 moduleApi)
    {
        if (moduleApi.CopyCanonicalManifest is null)
            throw new InvalidOperationException("The app module has no canonical-manifest export.");

        uint requiredSize = 0;
        var sizeStatus = moduleApi.CopyCanonicalManifest(null, 0, &requiredSize);
        if (sizeStatus != (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success ||
            requiredSize is 0 or > MaximumManifestBytes)
            throw new InvalidOperationException(
                "The app module returned an invalid canonical-manifest size.");

        var manifest = new byte[requiredSize];
        fixed (byte* destination = manifest)
        {
            var copyStatus = moduleApi.CopyCanonicalManifest(
                destination, requiredSize, &requiredSize);
            if (copyStatus != (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success)
                throw new InvalidOperationException(
                    "The app module canonical manifest could not be copied.");
        }
        return manifest;
    }

    private static bool ManifestMatchesSchemaHash(
        ReadOnlySpan<byte> manifest,
        ReadOnlySpan<byte> moduleHash)
    {
        if (moduleHash.Length != 32)
            return false;
        try
        {
            using var document = JsonDocument.Parse(manifest.ToArray());
            if (!document.RootElement.TryGetProperty("schemaHash", out var hashElement) ||
                hashElement.ValueKind != JsonValueKind.String ||
                hashElement.GetString() is not { Length: 64 } hash)
                return false;
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(hash), moduleHash);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static int DisposeBeforeLoad(
        ulong endpointToken,
        ManagedAppEndpointBindingLease? binding,
        int status,
        ulong* opaqueHostLease,
        ManagedAppModuleLease? moduleLease = null)
    {
        try
        {
            moduleLease?.ReleaseAfterClose();
            binding?.Dispose();
            return status;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Managed app endpoint rollback failed ({exception.GetType().Name}).");
            return Retain(
                new ManagedAppHostRegistrationLease(
                    endpointToken, moduleLease, registration: null, binding,
                    permanentQuarantine: false),
                opaqueHostLease,
                (int)ManagedAppHostStatusV1.ManagedFailure);
        }
    }

    private static int RetainWithoutRegistration(
        ManagedAppModuleLease moduleLease,
        ManagedAppEndpointBindingLease binding,
        bool permanentQuarantine,
        ulong* opaqueHostLease,
        int status) =>
        Retain(
            new ManagedAppHostRegistrationLease(
                binding.Descriptor.EndpointToken,
                moduleLease, registration: null, binding, permanentQuarantine),
            opaqueHostLease,
            status);

    private static int Retain(
        ManagedAppHostRegistrationLease lease,
        ulong* opaqueHostLease,
        int status)
    {
        while (true)
        {
            var id = unchecked((ulong)Interlocked.Increment(ref _nextLeaseId));
            if (id == 0 || ClosedLeases.ContainsKey(id))
                continue;
            if (Leases.TryAdd(id, lease))
            {
                *opaqueHostLease = id;
                return status;
            }
        }
    }

    internal static int MapBridgeStatus(int status) => status switch
    {
        (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success =>
            (int)ManagedAppHostStatusV1.Success,
        (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.InvalidArgument =>
            (int)ManagedAppHostStatusV1.InvalidArgument,
        (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.UnsupportedVersion =>
            (int)ManagedAppHostStatusV1.UnsupportedVersion,
        (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.BufferTooSmall =>
            (int)ManagedAppHostStatusV1.BufferTooSmall,
        (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.ModuleUnavailable =>
            (int)ManagedAppHostStatusV1.ModuleUnavailable,
        (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.SchemaMismatch =>
            (int)ManagedAppHostStatusV1.SchemaMismatch,
        (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.RegistrationFailure =>
            (int)ManagedAppHostStatusV1.RegistrationFailure,
        (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Busy =>
            (int)ManagedAppHostStatusV1.Busy,
        (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.StaleHandle =>
            (int)ManagedAppHostStatusV1.StaleHandle,
        _ => (int)ManagedAppHostStatusV1.ManagedFailure
    };

    private static int MapException(Exception exception) =>
        exception switch
        {
            FileNotFoundException or DirectoryNotFoundException =>
                (int)ManagedAppHostStatusV1.ModuleUnavailable,
            BadImageFormatException =>
                (int)ManagedAppHostStatusV1.ModuleUnavailable,
            FileLoadException =>
                (int)ManagedAppHostStatusV1.SchemaMismatch,
            ArgumentException =>
                (int)ManagedAppHostStatusV1.InvalidArgument,
            _ => (int)ManagedAppHostStatusV1.ManagedFailure
        };

    private static bool IsValidBuffer(byte* pointer, uint length, int maximumLength) =>
        length > 0 &&
        length <= maximumLength &&
        pointer is not null;

    private static bool IsStrictUtf8(ReadOnlySpan<byte> value)
    {
        if (value.Contains((byte)0))
            return false;
        try
        {
            _ = StrictUtf8.GetCharCount(value);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsValidProviderTypeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
            return false;
        try
        {
            return StrictUtf8.GetByteCount(value) <= MaximumProviderNameBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryDecodeStrictUtf8(byte* pointer, uint length, out string value)
    {
        try
        {
            value = StrictUtf8.GetString(new ReadOnlySpan<byte>(pointer, checked((int)length)));
            return !value.Contains('\0');
        }
        catch (DecoderFallbackException)
        {
            value = string.Empty;
            return false;
        }
        catch (OverflowException)
        {
            value = string.Empty;
            return false;
        }
    }
}

internal sealed class ManagedAppHostRegistrationLease(
    ulong endpointToken,
    ManagedAppModuleLease? moduleLease,
    GeneratedHostRegistration? registration,
    ManagedAppEndpointBindingLease? binding,
    bool permanentQuarantine)
{
    private readonly object _gate = new();
    private readonly ulong _endpointToken = endpointToken;
    private ManagedAppModuleLease? _moduleLease = moduleLease;
    private readonly GeneratedHostRegistration? _registration = registration;
    private ManagedAppEndpointBindingLease? _binding = binding;
    private readonly bool _permanentQuarantine = permanentQuarantine;
    private bool _bridgeClosed;
    private bool _closeInProgress;
    private bool _closed;

    public int Close()
    {
        lock (_gate)
        {
            if (_closed)
                return (int)ManagedAppHostStatusV1.AlreadyClosed;
            if (_permanentQuarantine)
                return (int)ManagedAppHostStatusV1.ManagedFailure;
            if (_closeInProgress)
                return (int)ManagedAppHostStatusV1.Busy;
            _closeInProgress = true;
        }

        try
        {
            _ = ManagedAppEndpointTransportApiV1Exports.BeginHostClose(_endpointToken);
            if (_binding?.Registry is IManagedAppEndpointHostRegistry routeRegistry)
            {
                routeRegistry.BeginRouteDrain();
            }

            if (ManagedAppEndpointTransportApiV1Exports.BeginHostClose(_endpointToken) ==
                    (int)ManagedAppEndpointTransportStatusV1.Busy ||
                (_binding?.Registry is IManagedAppEndpointHostRegistry activeRouteRegistry &&
                 activeRouteRegistry.ActiveRouteLeaseCount != 0))
                return (int)ManagedAppHostStatusV1.Busy;

            if (_registration is not null && !_bridgeClosed)
            {
                var bridgeStatus = _registration.Close();
                if (bridgeStatus != (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success)
                    return ManagedAppHostApiV1Exports.MapBridgeStatus(bridgeStatus);
                _bridgeClosed = true;
                _moduleLease = null;
            }
            else if (_registration is null)
            {
                _moduleLease?.ReleaseAfterClose();
                _moduleLease = null;
            }

            _binding?.Dispose();
            _binding = null;
            if (!ManagedAppEndpointTransportApiV1Exports.ReleaseAfterHostClose(_endpointToken))
                return (int)ManagedAppHostStatusV1.ManagedFailure;
            _closed = true;
            return (int)ManagedAppHostStatusV1.Success;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Managed app endpoint close failed ({exception.GetType().Name}); lease retained.");
            return (int)ManagedAppHostStatusV1.ManagedFailure;
        }
        finally
        {
            lock (_gate)
                _closeInProgress = false;
        }
    }
}
