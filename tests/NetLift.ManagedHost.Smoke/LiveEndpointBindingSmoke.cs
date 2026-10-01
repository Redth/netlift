#if NETLIFT_MANAGED_HOST_DISPATCH_SMOKE
extern alias OldAsyncRuntime;
extern alias NewAsyncRuntime;
#endif

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using NetLift;
using NetLift.ManagedHost;
using NetLift.Negotiation;
using NetLift.Negotiation.NativeHost.AsyncRuntime;
using OldHost = NetLift.Negotiation.NativeHost.Managed.Old;
using NewHost = NetLift.Negotiation.NativeHost.Managed.New;

internal static class LiveEndpointBindingSmoke
{
    private const uint Success = 0;
    private const uint SchemaBufferTooSmall = 12;
    private const string ProviderTypeName =
        "NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider";
    private static readonly ConcurrentDictionary<ulong, TaskCompletionSource<TransportNativeRequest>>
        TransportRequests = new();
    private static readonly ConcurrentDictionary<ulong, TaskCompletionSource<TransportPublishedResult>>
        TransportResults = new();
    private static ManualResetEventSlim? _transportCallbackRelease;
#if NETLIFT_MANAGED_HOST_DISPATCH_SMOKE
    private const int Ok = 0;
    private const int StaleHandle = 10;
    private static TaskCompletionSource<ulong>? _nativeRequestSeen;
    private static TaskCompletionSource<PublishedResult>? _publishedResult;
    private static ulong _expectedRegistrationToken;
    private static ulong _expectedRegistrationGeneration;
    private static ulong _expectedOperationToken;
    private static uint _expectedNativeServiceId;
    private static uint _expectedNativeMethodId;
#endif

    public static async Task RunSourceAuthorityAsync(bool isNew, string moduleAssemblyPath)
    {
        ILiveEndpointApi localEndpoint = isNew ? new NewEndpointApi() : new OldEndpointApi();
        ILiveEndpointApi peerEndpoint = isNew ? new OldEndpointApi() : new NewEndpointApi();
        var localManifest = localEndpoint.GetManifest();
        var peerManifest = peerEndpoint.GetManifest();
        var peerManifestForStart = peerManifest.ToArray();
        var localContract = NegotiationManifest.LoadEmbedded(localManifest);
        var peerContract = NegotiationManifest.LoadEmbedded(peerManifest);
        var peerHello = CompatibleStartPeerHelloV1.Encode(peerContract);
        var trustedPeerHash = Convert.FromHexString(peerContract.SchemaHash);
        var authority = ManagedAppEndpointAuthority.Process;
        var provider = authority.BindingProvider;

        Require(ReferenceEquals(
                ManagedAppHostApiV1Exports.ConfiguredBindingProvider,
                provider),
            "ManagedAppHostApiV1 is not bound to the source-owned process endpoint authority.");
        Require(authority.CreateEndpoint(
                    [],
                    true,
                    trustedPeerHash,
                    peerContract.ContractMajor,
                    peerContract.ContractMinor,
                    out _) == ManagedAppEndpointAuthorityStatus.InvalidArgument,
            "The source endpoint authority accepted an empty local contract.");

        var incorrectPeerPin = trustedPeerHash.ToArray();
        incorrectPeerPin[0] ^= 0x80;
        Require(authority.CreateEndpoint(
                    localManifest,
                    true,
                    incorrectPeerPin,
                    peerContract.ContractMajor,
                    peerContract.ContractMinor,
                    out var rejectedToken) == ManagedAppEndpointAuthorityStatus.Success,
            "The source endpoint authority did not create a peer-pin negative fixture.");
        Require(authority.StartEndpoint(
                    rejectedToken,
                    peerManifestForStart,
                    peerHello,
                    true,
                    out _,
                    out var rejectedError) ==
                    ManagedAppEndpointAuthorityStatus.NegotiationRejected &&
                rejectedError == NegotiationErrorCode.UntrustedPeerManifest &&
                provider.Acquire(rejectedToken) is null &&
                provider.RegisteredRouteCount == 0 &&
                provider.OutstandingBindingLeaseCount == 0 &&
                provider.OutstandingResultContextLeaseCount == 0,
            "An untrusted peer pin published source-owned endpoint authority.");
        Require(authority.CloseEndpoint(rejectedToken) ==
                    ManagedAppEndpointAuthorityStatus.Success,
            "The source endpoint authority could not close after trusted-peer rejection.");

        var localManifestForRegistration = localManifest.ToArray();
        Require(authority.CreateEndpoint(
                    localManifest,
                    true,
                    trustedPeerHash,
                    peerContract.ContractMajor,
                    peerContract.ContractMinor,
                    out var failedPublishToken) == ManagedAppEndpointAuthorityStatus.Success,
            "The source authority did not create a publication-rollback fixture.");
        authority.BeforePublishForTesting = () =>
            throw new InvalidOperationException("Injected source authority publish failure.");
        var originalError = Console.Error;
        using var publishFailureLog = new StringWriter();
        Console.SetError(publishFailureLog);
        try
        {
            Require(authority.StartEndpoint(
                        failedPublishToken,
                        peerManifestForStart,
                        peerHello,
                        true,
                        out _,
                        out _) == ManagedAppEndpointAuthorityStatus.ManagedFailure &&
                    provider.Acquire(failedPublishToken) is null &&
                    provider.RegisteredRouteCount == 0 &&
                    provider.OutstandingBindingLeaseCount == 0 &&
                    provider.OutstandingResultContextLeaseCount == 0,
                "A failed source publication left an endpoint descriptor or lease.");
        }
        finally
        {
            authority.BeforePublishForTesting = null;
            Console.SetError(originalError);
        }
        Require(publishFailureLog.ToString().Contains(
                    "ManagedAppEndpointAuthority.StartEndpoint failed (InvalidOperationException): Injected source authority publish failure.",
                    StringComparison.Ordinal),
            "A source publication exception was not logged with its type and message.");
        Require(authority.CloseEndpoint(failedPublishToken) ==
                    ManagedAppEndpointAuthorityStatus.Success,
            "The source authority could not roll back and close after publication failure.");

        Require(authority.CreateEndpoint(
                    localManifest,
                    true,
                    trustedPeerHash,
                    peerContract.ContractMajor,
                    peerContract.ContractMinor,
                    out var failedStartToken) == ManagedAppEndpointAuthorityStatus.Success,
            "The source authority did not create a negotiation-exception fixture.");
        authority.BeforeStartForTesting = () =>
            throw new InvalidOperationException("Injected source CompatibleStart failure.");
        originalError = Console.Error;
        using var startFailureLog = new StringWriter();
        Console.SetError(startFailureLog);
        try
        {
            Require(authority.StartEndpoint(
                        failedStartToken,
                        peerManifestForStart,
                        peerHello,
                        true,
                        out _,
                        out _) == ManagedAppEndpointAuthorityStatus.ManagedFailure &&
                    provider.Acquire(failedStartToken) is null &&
                    provider.RegisteredRouteCount == 0 &&
                    provider.OutstandingBindingLeaseCount == 0 &&
                    provider.OutstandingResultContextLeaseCount == 0,
                "A thrown compatible-start failure left an endpoint descriptor or lease.");
        }
        finally
        {
            authority.BeforeStartForTesting = null;
            Console.SetError(originalError);
        }
        Require(startFailureLog.ToString().Contains(
                    "ManagedAppEndpointAuthority.StartEndpoint failed (InvalidOperationException): Injected source CompatibleStart failure.",
                    StringComparison.Ordinal) &&
                authority.CloseEndpoint(failedStartToken) ==
                    ManagedAppEndpointAuthorityStatus.Success,
            "A thrown compatible-start failure was not logged or left the endpoint stuck in Starting.");

        Require(authority.CreateEndpoint(
                    localManifest,
                    true,
                    trustedPeerHash,
                    peerContract.ContractMajor,
                    peerContract.ContractMinor,
                    out var endpointToken) == ManagedAppEndpointAuthorityStatus.Success,
            "The source-owned endpoint authority failed to create a trusted endpoint.");
        Require(authority.CreateEndpoint(
                    localManifest,
                    true,
                    trustedPeerHash,
                    peerContract.ContractMajor,
                    peerContract.ContractMinor,
                    out var secondEndpointToken) == ManagedAppEndpointAuthorityStatus.Busy &&
                secondEndpointToken == 0,
            "The source-owned process endpoint authority exceeded its provisional single-endpoint cap.");

        using var publishReached = new ManualResetEventSlim();
        using var allowPublish = new ManualResetEventSlim();
        LiftAsyncCallbackV1HostDescriptor? startedDescriptor = null;
        authority.BeforePublishForTesting = () =>
        {
            publishReached.Set();
            if (!allowPublish.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Timed out waiting to publish source endpoint authority.");
        };

        try
        {
            var startTask = Task.Run(() =>
            {
                var status = authority.StartEndpoint(
                    endpointToken,
                    peerManifestForStart,
                    peerHello,
                    true,
                    out var descriptor,
                    out var errorCode);
                return (status, descriptor, errorCode);
            });
            Require(publishReached.Wait(TimeSpan.FromSeconds(5)),
                "Source endpoint Start did not reach the publication gate.");
            Require(authority.CloseEndpoint(endpointToken) ==
                        ManagedAppEndpointAuthorityStatus.Busy &&
                    provider.Acquire(endpointToken) is null,
                "Source Close did not reject a concurrent in-progress Start.");
            allowPublish.Set();
            var startResult = await startTask.WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
            startedDescriptor = startResult.descriptor
                ?? throw new InvalidOperationException(
                    "Source endpoint Start returned no descriptor.");
            Require(startResult.status == ManagedAppEndpointAuthorityStatus.Success &&
                    startResult.errorCode is null,
                $"Source endpoint Start failed after its close race (status={startResult.status}).");
        }
        finally
        {
            allowPublish.Set();
            authority.BeforePublishForTesting = null;
        }

        Require(authority.StartEndpoint(
                    endpointToken,
                    peerManifestForStart,
                    peerHello,
                    true,
                    out _,
                    out var duplicateStartError) ==
                    ManagedAppEndpointAuthorityStatus.NegotiationRejected &&
                duplicateStartError == NegotiationErrorCode.OptedOut,
            "The source endpoint authority allowed a second Start attempt.");
        localManifest[0] ^= 0x01;
        peerManifest[0] ^= 0x01;

        var descriptorLease = provider.Acquire(endpointToken)
            ?? throw new InvalidOperationException(
                "The source authority did not publish a live endpoint binding.");
        var immutableStartDescriptor = startedDescriptor
            ?? throw new InvalidOperationException("Source Start lost its returned descriptor.");
        Require(descriptorLease.Descriptor.EndpointToken == endpointToken &&
                descriptorLease.Descriptor.PeerRequestSchema.Span.SequenceEqual(
                    immutableStartDescriptor.PeerRequestSchema.Span) &&
                descriptorLease.Descriptor.ManagedResultSchema.Span.SequenceEqual(
                    immutableStartDescriptor.ManagedResultSchema.Span) &&
                descriptorLease.Descriptor.PeerRequestSchema.Span[..4].SequenceEqual("NLPS"u8) &&
                descriptorLease.Descriptor.ManagedResultSchema.Span[..4].SequenceEqual("NLMR"u8),
            "The source authority did not issue stable NLPS/NLMR descriptor snapshots from Start.");
        var descriptor = descriptorLease.Descriptor;
        descriptorLease.Dispose();

        var api = QueryManagedAppHostApi();
        Require(AreSameApiTables(api, QueryForwardedManagedAppHostApi(isNew)),
            "The same-component GetApiV1 seam did not expose the source authority's host table.");
        var tamperedNlmr = descriptor.ManagedResultSchema.ToArray();
        tamperedNlmr[^1] ^= 1;
        var mismatchStatus = CallRegister(
            api,
            endpointToken,
            descriptor.PeerRequestSchema.ToArray(),
            tamperedNlmr,
            localManifestForRegistration,
            moduleAssemblyPath,
            ProviderTypeName,
            out var mismatchLease);
        Require(mismatchStatus == (int)ManagedAppHostStatusV1.SchemaMismatch &&
                mismatchLease == 0 &&
                provider.RegisteredRouteCount == 0 &&
                provider.OutstandingBindingLeaseCount == 0 &&
                provider.OutstandingResultContextLeaseCount == 0,
            "A C-supplied NLMR mismatch altered or published the source-issued descriptor.");

        var registerStatus = CallRegister(
            api,
            endpointToken,
            descriptor.PeerRequestSchema.ToArray(),
            descriptor.ManagedResultSchema.ToArray(),
            localManifestForRegistration,
            moduleAssemblyPath,
            ProviderTypeName,
            out var leaseId);
        Require(registerStatus == (int)ManagedAppHostStatusV1.Success &&
                leaseId != 0 &&
                provider.RegisteredRouteCount == 1,
            $"Generated module registration from source-issued descriptors failed ({registerStatus}).");
        Require(provider.TryAcquireRegisteredRoute(
                    endpointToken,
                    out var routeDescriptor,
                    out _,
                    out var routeLease) &&
                routeLease is not null &&
                routeDescriptor.ManagedResultSchema.Span.SequenceEqual(
                    descriptor.ManagedResultSchema.Span),
            "The generated module route did not retain the source-issued endpoint descriptor.");
        var activeRouteLease = routeLease
            ?? throw new InvalidOperationException("A registered route had no active lease.");

        Require(CallClose(api, leaseId) == (int)ManagedAppHostStatusV1.Busy &&
                authority.CloseEndpoint(endpointToken) ==
                    ManagedAppEndpointAuthorityStatus.Busy &&
                provider.ActiveRouteLeaseCount == 1 &&
                provider.OutstandingBindingLeaseCount == 2 &&
                provider.OutstandingResultContextLeaseCount == 2,
            "Busy close did not retain the active source route and descriptor leases.");
        Require(!provider.TryAcquireRegisteredRoute(
                    endpointToken, out _, out _, out _),
            "A source endpoint route remained admissible after close began draining it.");
        activeRouteLease.Dispose();
        Require(CallClose(api, leaseId) == (int)ManagedAppHostStatusV1.Success &&
                authority.CloseEndpoint(endpointToken) ==
                    ManagedAppEndpointAuthorityStatus.Success &&
                provider.Acquire(endpointToken) is null &&
                provider.ActiveEndpointCount == 0 &&
                provider.OutstandingBindingLeaseCount == 0 &&
                provider.OutstandingResultContextLeaseCount == 0 &&
                provider.RegisteredRouteCount == 0 &&
                authority.EndpointCount == 0 &&
                ManagedAppHostApiV1Exports.OutstandingLeaseCount == 0 &&
                GeneratedHostRegistration.OutstandingRegistrationCount == 0,
            "Source endpoint close did not drain module, route and result-context ownership.");
        Require(authority.CloseEndpoint(endpointToken) ==
                    ManagedAppEndpointAuthorityStatus.StaleHandle &&
                authority.StartEndpoint(
                    endpointToken,
                    peerManifest,
                    peerHello,
                    true,
                    out _,
                    out _) == ManagedAppEndpointAuthorityStatus.StaleHandle,
            "A closed source endpoint token remained usable.");
        Require(authority.CreateEndpoint(
                    localManifestForRegistration,
                    true,
                    trustedPeerHash,
                    peerContract.ContractMajor,
                    peerContract.ContractMinor,
                    out _) == ManagedAppEndpointAuthorityStatus.InvalidState,
            "A second product endpoint was admitted after the process-pinned module identity was consumed.");
        Console.WriteLine(
            "PASS: source-owned OLD/NEW endpoint authority, trusted NLPS/NLMR, same-component GetApi identity, Busy close/drain, Start/Close race, rollback and zero leases.");
    }

    public static async Task RunSourceAuthorityCdeclAsync(
        bool isNew,
        string moduleAssemblyPath)
    {
        ILiveEndpointApi localEndpoint = isNew ? new NewEndpointApi() : new OldEndpointApi();
        ILiveEndpointApi peerEndpoint = isNew ? new OldEndpointApi() : new NewEndpointApi();
        var localManifest = localEndpoint.GetManifest();
        var peerManifest = peerEndpoint.GetManifest();
        var peerHello = CompatibleStartPeerHelloV1.Encode(
            NegotiationManifest.LoadEmbedded(peerManifest));
        var peerContract = NegotiationManifest.LoadEmbedded(peerManifest);
        var trustedPeerHash = Convert.FromHexString(peerContract.SchemaHash);
        var expectedLocalManifest = localManifest.ToArray();
        var authority = ManagedAppEndpointAuthority.Process;
        var provider = authority.BindingProvider;
        var api = QueryManagedAppEndpointAuthorityApi();
        var hostApi = QueryManagedAppHostApi();

        Require(ReferenceEquals(
                    typeof(ManagedAppEndpointAuthorityApiV1Exports).Assembly,
                    typeof(ManagedAppHostApiV1Exports).Assembly) &&
                ReferenceEquals(ManagedAppHostApiV1Exports.ConfiguredBindingProvider, provider),
            "The endpoint authority and ManagedAppHost APIs did not share one ManagedHost component/provider.");
        Require(AreSameApiTables(hostApi, QueryForwardedManagedAppHostApi(isNew)),
            "The same-component ManagedAppHost GetApiV1 table changed during authority API query.");
        TestAuthorityApiBadInputs(
            api, localManifest, peerManifest, peerHello, trustedPeerHash,
            peerContract.ContractMajor, peerContract.ContractMinor);

        var incorrectPeerPin = trustedPeerHash.ToArray();
        incorrectPeerPin[0] ^= 0x80;
        Require(CallAuthorityCreate(
                    api,
                    localManifest,
                    1,
                    incorrectPeerPin,
                    peerContract.ContractMajor,
                    peerContract.ContractMinor,
                    out var rejectedToken) ==
                (int)ManagedAppEndpointAuthorityStatus.Success,
            "Cdecl Create did not accept a valid trusted local policy and peer pin.");
        Require(AuthoritySnapshotStatus(
                    api, rejectedToken, ManagedAppEndpointAuthorityApiV1Exports.PeerRequestSchemaSnapshot) ==
                (int)ManagedAppEndpointAuthorityStatus.InvalidState &&
                CallAuthorityStart(api, rejectedToken, peerManifest, peerHello, 1) ==
                (int)ManagedAppEndpointAuthorityStatus.NegotiationRejected &&
                AuthoritySnapshotStatus(
                    api, rejectedToken, ManagedAppEndpointAuthorityApiV1Exports.PeerRequestSchemaSnapshot) ==
                (int)ManagedAppEndpointAuthorityStatus.InvalidState &&
                CallAuthorityClose(api, rejectedToken) ==
                (int)ManagedAppEndpointAuthorityStatus.Success &&
                authority.EndpointCount == 0 &&
                provider.ActiveEndpointCount == 0 &&
                provider.OutstandingBindingLeaseCount == 0 &&
                provider.OutstandingResultContextLeaseCount == 0,
            "Cdecl trust rejection published a descriptor, retained a lease, or failed to close.");

        Require(CallAuthorityCreate(
                    api,
                    localManifest,
                    1,
                    trustedPeerHash,
                    peerContract.ContractMajor,
                    peerContract.ContractMinor,
                    out var rollbackToken) ==
                (int)ManagedAppEndpointAuthorityStatus.Success,
            "Cdecl Create failed for the pre-publication rollback case.");
        var originalError = Console.Error;
        using var rollbackLog = new StringWriter();
        Console.SetError(rollbackLog);
        authority.BeforePublishForTesting = () =>
            throw new InvalidOperationException("Injected Cdecl authority publication failure.");
        try
        {
            Require(CallAuthorityStart(api, rollbackToken, peerManifest, peerHello, 1) ==
                        (int)ManagedAppEndpointAuthorityStatus.ManagedFailure &&
                    AuthoritySnapshotStatus(
                        api, rollbackToken, ManagedAppEndpointAuthorityApiV1Exports.ManagedResultSchemaSnapshot) ==
                        (int)ManagedAppEndpointAuthorityStatus.InvalidState &&
                    CallAuthorityClose(api, rollbackToken) ==
                        (int)ManagedAppEndpointAuthorityStatus.Success,
                "Cdecl publication rollback returned an unexpected status or left the endpoint open.");
        }
        finally
        {
            authority.BeforePublishForTesting = null;
            Console.SetError(originalError);
        }
        Require(rollbackLog.ToString().Contains(
                    "ManagedAppEndpointAuthority.StartEndpoint failed (InvalidOperationException): Injected Cdecl authority publication failure.",
                    StringComparison.Ordinal) &&
                authority.EndpointCount == 0 &&
                provider.ActiveEndpointCount == 0 &&
                provider.OutstandingBindingLeaseCount == 0 &&
                provider.OutstandingResultContextLeaseCount == 0,
            "Cdecl publication rollback did not log the failure or release endpoint roots.");

        for (var index = 0; index < 16; index++)
        {
            Require(CallAuthorityCreate(
                        api,
                        localManifest,
                        1,
                        trustedPeerHash,
                        peerContract.ContractMajor,
                        peerContract.ContractMinor,
                        out var closedToken) ==
                    (int)ManagedAppEndpointAuthorityStatus.Success &&
                    CallAuthorityClose(api, closedToken) ==
                    (int)ManagedAppEndpointAuthorityStatus.Success &&
                    authority.EndpointCount == 0 &&
                    provider.ActiveEndpointCount == 0 &&
                    provider.OutstandingBindingLeaseCount == 0 &&
                    provider.OutstandingResultContextLeaseCount == 0,
                $"Cdecl close cycle {index} retained a terminal endpoint record or lease.");
        }

        Require(CallAuthorityCreate(
                    api,
                    localManifest,
                    1,
                    trustedPeerHash,
                    peerContract.ContractMajor,
                    peerContract.ContractMinor,
                    out var endpointToken) ==
                (int)ManagedAppEndpointAuthorityStatus.Success,
            "Cdecl Create failed for the successful OLD/NEW route case.");
        Require(CallAuthorityCreate(
                    api,
                    localManifest,
                    1,
                    trustedPeerHash,
                    peerContract.ContractMajor,
                    peerContract.ContractMinor,
                    out var secondToken) ==
                (int)ManagedAppEndpointAuthorityStatus.Busy &&
                secondToken == 0,
            "A concurrent Cdecl Create was not rejected with Busy.");
        Require(AuthoritySnapshotStatus(
                    api, endpointToken, ManagedAppEndpointAuthorityApiV1Exports.LocalCanonicalManifestSnapshot) ==
                (int)ManagedAppEndpointAuthorityStatus.InvalidState,
            "Cdecl snapshot access succeeded before endpoint Start.");

        using var publishReached = new ManualResetEventSlim();
        using var allowPublish = new ManualResetEventSlim();
        authority.BeforePublishForTesting = () =>
        {
            publishReached.Set();
            if (!allowPublish.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Timed out waiting at the Cdecl Start publication barrier.");
        };
        try
        {
            var startTask = Task.Run(() =>
                CallAuthorityStart(api, endpointToken, peerManifest, peerHello, 1));
            Require(publishReached.Wait(TimeSpan.FromSeconds(5)),
                "Cdecl Start did not reach its deterministic publication barrier.");
            Require(CallAuthorityClose(api, endpointToken) ==
                        (int)ManagedAppEndpointAuthorityStatus.Busy &&
                    AuthoritySnapshotStatus(
                        api, endpointToken,
                        ManagedAppEndpointAuthorityApiV1Exports.LocalCanonicalManifestSnapshot) ==
                        (int)ManagedAppEndpointAuthorityStatus.Busy,
                "Cdecl Close/snapshot did not report Busy while Start was in progress.");
            allowPublish.Set();
            Require(await startTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false) ==
                    (int)ManagedAppEndpointAuthorityStatus.Success,
                "Cdecl Start did not complete after the close race was released.");
        }
        finally
        {
            allowPublish.Set();
            authority.BeforePublishForTesting = null;
        }

        var localSnapshot = CopyAuthoritySnapshot(
            api, endpointToken, ManagedAppEndpointAuthorityApiV1Exports.LocalCanonicalManifestSnapshot);
        var nlps = CopyAuthoritySnapshot(
            api, endpointToken, ManagedAppEndpointAuthorityApiV1Exports.PeerRequestSchemaSnapshot);
        var nlmr = CopyAuthoritySnapshot(
            api, endpointToken, ManagedAppEndpointAuthorityApiV1Exports.ManagedResultSchemaSnapshot);
        Require(localSnapshot.AsSpan().SequenceEqual(expectedLocalManifest) &&
                nlps.AsSpan(0, 4).SequenceEqual("NLPS"u8) &&
                nlmr.AsSpan(0, 4).SequenceEqual("NLMR"u8) &&
                ManagedAppEndpointAuthorityApiV1Exports.OutstandingBufferCount == 0,
            "Cdecl getters did not return copied local/NLPS/NLMR snapshots with balanced buffer ownership.");

        localManifest[0] ^= 1;
        peerManifest[0] ^= 1;
        peerHello[0] ^= 1;
        Require(CopyAuthoritySnapshot(
                    api, endpointToken,
                    ManagedAppEndpointAuthorityApiV1Exports.LocalCanonicalManifestSnapshot)
                    .AsSpan().SequenceEqual(expectedLocalManifest),
            "Cdecl endpoint snapshots changed after caller-owned input buffers were mutated.");
        Require(CallAuthorityStart(api, endpointToken, peerEndpoint.GetManifest(), peerHello, 1) ==
                (int)ManagedAppEndpointAuthorityStatus.NegotiationRejected,
            "Cdecl endpoint accepted a second Start attempt.");

        var descriptor = provider.Acquire(endpointToken)
            ?? throw new InvalidOperationException("Cdecl Start did not publish the source binding.");
        Require(descriptor.Descriptor.PeerRequestSchema.Span.SequenceEqual(nlps) &&
                descriptor.Descriptor.ManagedResultSchema.Span.SequenceEqual(nlmr),
            "Cdecl getters differed from the independently acquired source descriptor.");
        descriptor.Dispose();

        var tamperedNlmr = nlmr.ToArray();
        tamperedNlmr[^1] ^= 1;
        var mismatchStatus = CallRegister(
            hostApi,
            endpointToken,
            nlps,
            tamperedNlmr,
            localSnapshot,
            moduleAssemblyPath,
            ProviderTypeName,
            out var mismatchLease);
        Require(mismatchStatus == (int)ManagedAppHostStatusV1.SchemaMismatch &&
                mismatchLease == 0 &&
                provider.RegisteredRouteCount == 0 &&
                provider.OutstandingBindingLeaseCount == 0 &&
                provider.OutstandingResultContextLeaseCount == 0,
            "Cdecl authority snapshots were not independently validated before module route publication.");

        var registerStatus = CallRegister(
            hostApi,
            endpointToken,
            nlps,
            nlmr,
            localSnapshot,
            moduleAssemblyPath,
            ProviderTypeName,
            out var hostLease);
        Require(registerStatus == (int)ManagedAppHostStatusV1.Success &&
                hostLease != 0 &&
                provider.RegisteredRouteCount == 1 &&
                authority.EndpointCount == 1,
            $"API-V1 registration from Cdecl authority snapshots failed ({registerStatus}).");
        await RunSourceEndpointTransportAsync(
            isNew, endpointToken, hostApi, hostLease, provider, nlmr).ConfigureAwait(false);
        Require(provider.RegisteredRouteCount == 0 &&
                provider.ActiveRouteLeaseCount == 0 &&
                CallAuthorityClose(api, endpointToken) == (int)ManagedAppEndpointAuthorityStatus.Success &&
                CallAuthorityClose(api, endpointToken) == (int)ManagedAppEndpointAuthorityStatus.StaleHandle &&
                AuthoritySnapshotStatus(
                    api, endpointToken,
                    ManagedAppEndpointAuthorityApiV1Exports.LocalCanonicalManifestSnapshot) ==
                    (int)ManagedAppEndpointAuthorityStatus.StaleHandle &&
                provider.Acquire(endpointToken) is null &&
                provider.ActiveEndpointCount == 0 &&
                provider.RegisteredRouteCount == 0 &&
                provider.ActiveRouteLeaseCount == 0 &&
                provider.OutstandingBindingLeaseCount == 0 &&
                provider.OutstandingResultContextLeaseCount == 0 &&
                authority.EndpointCount == 0 &&
                ManagedAppEndpointAuthorityApiV1Exports.OutstandingBufferCount == 0 &&
                ManagedAppHostApiV1Exports.OutstandingLeaseCount == 0 &&
                GeneratedHostRegistration.OutstandingRegistrationCount == 0,
            "Cdecl close/drain did not remove the endpoint record or release all managed roots.");
        Require(CallAuthorityCreate(
                    api,
                    expectedLocalManifest,
                    1,
                    trustedPeerHash,
                    peerContract.ContractMajor,
                    peerContract.ContractMinor,
                    out var postLoadToken) ==
                (int)ManagedAppEndpointAuthorityStatus.InvalidState &&
                postLoadToken == 0,
            "Cdecl Create admitted a second endpoint identity after the one-shot module load.");

        Console.WriteLine(
            "PASS: private Cdecl authority and source transport query/bounds/trust/rollback/race, OLD/NEW generated Task/result and typed callback/reply/cancel, Busy revoke/drain/close and zero-root checks.");
    }

    private static async Task RunSourceEndpointTransportAsync(
        bool isNew,
        ulong endpointToken,
        ManagedAppHostApiV1 hostApi,
        ulong hostLease,
        ManagedAppEndpointBindingProvider provider,
        byte[] managedResultSchema)
    {
        const int transportSuccess = (int)ManagedAppEndpointTransportStatusV1.Success;
        const int transportInvalidArgument = (int)ManagedAppEndpointTransportStatusV1.InvalidArgument;
        const int transportNotReady = (int)ManagedAppEndpointTransportStatusV1.NotReady;
        const int transportBusy = (int)ManagedAppEndpointTransportStatusV1.Busy;
        const int transportStale = (int)ManagedAppEndpointTransportStatusV1.StaleHandle;
        const int transportUnavailable = (int)ManagedAppEndpointTransportStatusV1.Unavailable;
        const int transportCancelled = (int)ManagedAppEndpointTransportStatusV1.Cancelled;

        var transportApi = QueryManagedAppEndpointTransportApi();
        Require(provider.TryAcquireRegisteredRoute(
                    endpointToken,
                    out var descriptor,
                    out _,
                    out var routeLease) &&
                routeLease is not null &&
                descriptor.ManagedResultSchema.Span.SequenceEqual(managedResultSchema),
            "The source transport did not observe the API-V1 registered module's authority descriptor.");
        routeLease!.Dispose();

        Require(CallConfigureTransportCallbacks(transportApi, endpointToken, 0, 0) ==
                    transportInvalidArgument &&
                CallGetTransportOutstandingWorkWithNullOutputs(transportApi, endpointToken) ==
                    transportInvalidArgument &&
                CallStartTransportRequest(
                    transportApi, endpointToken, endpointToken + 2, 1, endpointToken + 90, null, 12) ==
                    transportInvalidArgument,
            "The source transport accepted invalid callback, output, or request buffers.");
        var notReadyRequest = GetTransportRequest(isNew, 30);
        Require(CallStartTransportRequest(
                    transportApi,
                    endpointToken, endpointToken + 2, 1, endpointToken + 91,
                    notReadyRequest, checked((uint)notReadyRequest.Length)) == transportNotReady,
            "The source transport accepted work before native callbacks were configured.");
        Require(CallConfigureTransportCallbacks(
                    transportApi,
                    endpointToken,
                    GetNativeRequestCallbackPointer(),
                    GetNativePublishResultCallbackPointer()) == transportSuccess &&
                CallConfigureTransportCallbacks(
                    transportApi,
                    endpointToken,
                    GetNativeRequestCallbackPointer(),
                    GetNativePublishResultCallbackPointer()) == transportInvalidArgument,
            "The source transport did not bind exactly one callback owner to the registered endpoint.");
        Require(GetTransportOutstandingWork(transportApi, endpointToken) ==
                    (0, 0, 0, 0),
            "A configured source transport unexpectedly retained work or route leases.");

        var registrationToken = endpointToken + 2;
        const ulong generation = 1;
        var nextOperation = endpointToken * 1000;

        var acceptedOperation = await StartTransportOperationAsync(
            transportApi, endpointToken, registrationToken, generation,
            ++nextOperation, isNew, 31).ConfigureAwait(false);
        Require(acceptedOperation.Request.EndpointToken == endpointToken &&
                acceptedOperation.Request.ServiceId == descriptor.NativeServiceId &&
                acceptedOperation.Request.MethodId == descriptor.NativeMethodId,
            "The source transport callback did not carry the bound endpoint's native service identity.");
        Require(ReplyTransportConfirmation(
                    transportApi, endpointToken, acceptedOperation.Request, accepted: true) ==
                transportSuccess,
            "The source transport did not accept a managed-origin typed Task<bool> reply.");
        var acceptedResult = await FinishTransportOperationAsync(transportApi, acceptedOperation)
            .ConfigureAwait(false);
        AssertTransportResult(acceptedResult, isNew, 31, accepted: true);

        var rejectedOperation = await StartTransportOperationAsync(
            transportApi, endpointToken, registrationToken, generation,
            ++nextOperation, isNew, 32).ConfigureAwait(false);
        Require(ReplyTransportConfirmation(
                    transportApi, endpointToken, rejectedOperation.Request, accepted: false) ==
                transportSuccess,
            "The source transport did not accept a false typed Task<bool> reply.");
        var rejectedResult = await FinishTransportOperationAsync(transportApi, rejectedOperation)
            .ConfigureAwait(false);
        AssertTransportResult(rejectedResult, isNew, 32, accepted: false);

        var replyWinner = await StartTransportOperationAsync(
            transportApi, endpointToken, registrationToken, generation,
            ++nextOperation, isNew, 33).ConfigureAwait(false);
        Require(ReplyTransportConfirmation(
                    transportApi, endpointToken, replyWinner.Request, accepted: true) ==
                    transportSuccess &&
                CancelTransportConfirmation(
                    transportApi, endpointToken, replyWinner.Request) == transportStale,
            "The reply-first typed callback race did not preserve the reply winner.");
        AssertTransportResult(
            await FinishTransportOperationAsync(transportApi, replyWinner).ConfigureAwait(false),
            isNew, 33, accepted: true);

        var cancelWinner = await StartTransportOperationAsync(
            transportApi, endpointToken, registrationToken, generation,
            ++nextOperation, isNew, 34).ConfigureAwait(false);
        Require(CancelTransportConfirmation(
                    transportApi, endpointToken, cancelWinner.Request) == transportSuccess &&
                ReplyTransportConfirmation(
                    transportApi, endpointToken, cancelWinner.Request, accepted: true) ==
                    transportStale,
            "The cancel-first typed callback race did not preserve the cancellation winner.");
        var cancelledConfirmationResult =
            await FinishTransportOperationAsync(transportApi, cancelWinner).ConfigureAwait(false);
        Require(cancelledConfirmationResult.Status == transportCancelled,
            "A native-origin cancellation did not cancel the generated Task<bool> callback.");

        var racedOperation = await StartTransportOperationAsync(
            transportApi, endpointToken, registrationToken, generation,
            ++nextOperation, isNew, 35).ConfigureAwait(false);
        var replyTask = Task.Run(() => ReplyTransportConfirmation(
            transportApi, endpointToken, racedOperation.Request, accepted: true));
        var cancelTask = Task.Run(() => CancelTransportConfirmation(
            transportApi, endpointToken, racedOperation.Request));
        var raceStatuses = await Task.WhenAll(replyTask, cancelTask).ConfigureAwait(false);
        Require(raceStatuses.Count(status => status == transportSuccess) == 1 &&
                raceStatuses.Count(status => status == transportStale) == 1,
            "The concurrent reply/cancel race did not have exactly one winner.");
        var racedResult = await FinishTransportOperationAsync(transportApi, racedOperation)
            .ConfigureAwait(false);
        Require(racedResult.Status is transportSuccess or transportCancelled,
            "The concurrent reply/cancel race published an invalid terminal status.");
        if (racedResult.Status == transportSuccess)
            AssertTransportResult(racedResult, isNew, 35, accepted: true);

        var cappedOperations = new List<PendingTransportOperation>();
        for (var index = 0; index < 32; index++)
        {
            var operationToken = ++nextOperation;
            var requestCompletion = new TaskCompletionSource<TransportNativeRequest>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var resultCompletion = new TaskCompletionSource<TransportPublishedResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Require(TransportRequests.TryAdd(operationToken, requestCompletion) &&
                    TransportResults.TryAdd(operationToken, resultCompletion),
                "A capped source transport operation token was reused.");
            var request = GetTransportRequest(isNew, 100 + index);
            var startStatus = CallStartTransportRequest(
                transportApi,
                endpointToken,
                registrationToken,
                generation,
                operationToken,
                request,
                checked((uint)request.Length));
            if (startStatus != transportSuccess)
            {
                TransportRequests.TryRemove(operationToken, out _);
                TransportResults.TryRemove(operationToken, out _);
                throw new InvalidOperationException(
                    $"The source transport rejected operation {index} before reaching its concurrency cap ({startStatus}).");
            }
            cappedOperations.Add(new PendingTransportOperation(
                operationToken, requestCompletion, resultCompletion));
        }

        await Task.WhenAll(cappedOperations.Select(operation =>
                operation.Request.Task.WaitAsync(TimeSpan.FromSeconds(10))))
            .ConfigureAwait(false);
        var cappedWork = GetTransportOutstandingWork(transportApi, endpointToken);
        Require(cappedWork.Operations == 32 &&
                cappedWork.Confirmations == 32 &&
                cappedWork.RouteLeases == 32,
            "The source transport did not account for its bounded pending-work set.");
        var overCapToken = ++nextOperation;
        var overCapRequest = GetTransportRequest(isNew, 132);
        Require(CallStartTransportRequest(
                    transportApi,
                    endpointToken,
                    registrationToken,
                    generation,
                    overCapToken,
                    overCapRequest,
                    checked((uint)overCapRequest.Length)) == transportBusy,
            "The source transport admitted more than its configured 32-operation bound.");
        foreach (var operation in cappedOperations)
        {
            Require(CallCancelTransportOperation(
                        transportApi, endpointToken, registrationToken, generation,
                        operation.OperationToken) == transportSuccess,
                "The source transport failed to cancel a capped pending operation.");
        }
        var cappedResults = await Task.WhenAll(cappedOperations.Select(operation =>
                operation.Result.Task.WaitAsync(TimeSpan.FromSeconds(10))))
            .ConfigureAwait(false);
        Require(cappedResults.All(result => result.Status == transportCancelled),
            "A capped pending operation did not publish its cancellation result.");
        foreach (var operation in cappedOperations)
        {
            TransportRequests.TryRemove(operation.OperationToken, out _);
            TransportResults.TryRemove(operation.OperationToken, out _);
        }
        await WaitForTransportDrainAsync(transportApi, endpointToken).ConfigureAwait(false);

        var cancelledOperation = await StartTransportOperationAsync(
            transportApi, endpointToken, registrationToken, generation,
            ++nextOperation, isNew, 36).ConfigureAwait(false);
        Require(CallCancelTransportOperation(
                    transportApi, endpointToken, registrationToken, generation,
                    cancelledOperation.Request.OperationToken) ==
                transportSuccess,
            "The source transport did not cancel an active generated Task operation.");
        Require((await FinishTransportOperationAsync(
                    transportApi, cancelledOperation).ConfigureAwait(false)).Status ==
                    transportCancelled,
            "Operation cancellation did not publish the cancelled terminal result.");

        using var callbackRelease = new ManualResetEventSlim();
        Volatile.Write(ref _transportCallbackRelease, callbackRelease);
        try
        {
            var pendingOperation = await StartTransportOperationAsync(
                transportApi, endpointToken, registrationToken, generation,
                ++nextOperation, isNew, 37).ConfigureAwait(false);
            var pendingWork = GetTransportOutstandingWork(transportApi, endpointToken);
            Require(pendingWork.Operations == 1 &&
                    pendingWork.Confirmations == 1 &&
                    pendingWork.Callbacks == 1 &&
                    pendingWork.RouteLeases == 1 &&
                    provider.OutstandingBindingLeaseCount == 2 &&
                    provider.OutstandingResultContextLeaseCount == 2,
                "The source transport did not retain route, callback, and result-context leases while pending.");

            var revokedRequest = GetTransportRequest(isNew, 38);
            Require(CallClose(hostApi, hostLease) == (int)ManagedAppHostStatusV1.Busy &&
                    CallRevokeTransportEndpoint(transportApi, endpointToken) == transportSuccess,
                "Host close did not revoke the route or return Busy for an outstanding callback.");
            var revokedStartStatus = CallStartTransportRequest(
                transportApi,
                endpointToken,
                registrationToken,
                generation,
                ++nextOperation,
                revokedRequest,
                checked((uint)revokedRequest.Length));
            Require(revokedStartStatus == transportUnavailable,
                "The source transport accepted a new operation after host close began draining.");
            callbackRelease.Set();
            var closeCancelledResult = await FinishTransportOperationAsync(transportApi, pendingOperation)
                .ConfigureAwait(false);
            Require(closeCancelledResult.Status == transportCancelled,
                "Host close/revoke did not cancel the outstanding generated route.");
            await WaitForTransportDrainAsync(transportApi, endpointToken).ConfigureAwait(false);
        }
        finally
        {
            callbackRelease.Set();
            Volatile.Write(ref _transportCallbackRelease, null);
        }

        Require(CallClose(hostApi, hostLease) == (int)ManagedAppHostStatusV1.Success &&
                provider.RegisteredRouteCount == 0 &&
                provider.ActiveRouteLeaseCount == 0 &&
                provider.OutstandingBindingLeaseCount == 0 &&
                provider.OutstandingResultContextLeaseCount == 0,
            "The API-V1 host lease did not close after source transport drain.");
        var closedWorkStatus = CallGetTransportOutstandingWork(
            transportApi, endpointToken,
            out var operations, out var confirmations, out var callbacks, out var routes);
        Require(closedWorkStatus == transportStale &&
                operations == 0 && confirmations == 0 && callbacks == 0 && routes == 0,
            "Host close did not release the callback owner or clear transport work outputs.");
    }

    private static async Task<(TransportNativeRequest Request, TaskCompletionSource<TransportPublishedResult> Result)>
        StartTransportOperationAsync(
            ManagedAppEndpointTransportApiV1 api,
            ulong endpointToken,
            ulong registrationToken,
            ulong generation,
            ulong operationToken,
            bool isNew,
            int resultCode)
    {
        var requestCompletion = new TaskCompletionSource<TransportNativeRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resultCompletion = new TaskCompletionSource<TransportPublishedResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Require(TransportRequests.TryAdd(operationToken, requestCompletion) &&
                TransportResults.TryAdd(operationToken, resultCompletion),
            "A source transport smoke operation token was reused.");
        try
        {
            var request = GetTransportRequest(isNew, resultCode);
            Require(CallStartTransportRequest(
                        api,
                        endpointToken,
                        registrationToken,
                        generation,
                        operationToken,
                        request,
                        checked((uint)request.Length)) ==
                    (int)ManagedAppEndpointTransportStatusV1.Success,
                "The source transport did not dispatch an API-V1 registered module route.");
            var nativeRequest = await requestCompletion.Task
                .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Require(nativeRequest.RegistrationToken == registrationToken &&
                    nativeRequest.RegistrationGeneration == generation &&
                    nativeRequest.OperationToken == operationToken &&
                    nativeRequest.RequestToken != 0 &&
                    nativeRequest.Payload.Length != 0,
                "The source transport native callback lost endpoint operation/correlation identity.");
            var work = GetTransportOutstandingWork(api, endpointToken);
            Require(work.Operations == 1 && work.Confirmations == 1 && work.RouteLeases == 1,
                "The source transport did not hold its operation, typed confirmation, and route leases.");
            return (nativeRequest, resultCompletion);
        }
        catch
        {
            TransportRequests.TryRemove(operationToken, out _);
            TransportResults.TryRemove(operationToken, out _);
            throw;
        }
    }

    private static async Task<TransportPublishedResult> FinishTransportOperationAsync(
        ManagedAppEndpointTransportApiV1 api,
        (TransportNativeRequest Request, TaskCompletionSource<TransportPublishedResult> Result) operation)
    {
        try
        {
            var result = await operation.Result.Task
                .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await WaitForTransportDrainAsync(api, operation.Request.EndpointToken)
                .ConfigureAwait(false);
            return result;
        }
        finally
        {
            TransportRequests.TryRemove(operation.Request.OperationToken, out _);
            TransportResults.TryRemove(operation.Request.OperationToken, out _);
        }
    }

    private static async Task WaitForTransportDrainAsync(
        ManagedAppEndpointTransportApiV1 api,
        ulong endpointToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (GetTransportOutstandingWork(api, endpointToken) == (0, 0, 0, 0))
                return;
            await Task.Delay(10).ConfigureAwait(false);
        }
        throw new InvalidOperationException("The source endpoint transport did not drain within one second.");
    }

    private static (uint Operations, uint Confirmations, uint Callbacks, uint RouteLeases)
        GetTransportOutstandingWork(ManagedAppEndpointTransportApiV1 api, ulong endpointToken)
    {
        var status = CallGetTransportOutstandingWork(
            api, endpointToken,
            out var operations, out var confirmations, out var callbacks, out var routeLeases);
        Require(status ==
                (int)ManagedAppEndpointTransportStatusV1.Success,
            "The source transport work-count query failed before host close.");
        return (operations, confirmations, callbacks, routeLeases);
    }

    private static unsafe int CallConfigureTransportCallbacks(
        ManagedAppEndpointTransportApiV1 api,
        ulong endpointToken,
        nint requestCallback,
        nint publishResultCallback) =>
        api.ConfigureNativeCallbacks(endpointToken, requestCallback, publishResultCallback);

    private static unsafe int CallStartTransportRequest(
        ManagedAppEndpointTransportApiV1 api,
        ulong endpointToken,
        ulong registrationToken,
        ulong generation,
        ulong operationToken,
        byte[]? request,
        uint requestLength)
    {
        if (request is null)
            return api.StartRequest(
                endpointToken, registrationToken, generation, operationToken, null, requestLength);
        fixed (byte* requestPointer = request)
            return api.StartRequest(
                endpointToken, registrationToken, generation, operationToken,
                requestPointer, requestLength);
    }

    private static unsafe int CallCancelTransportOperation(
        ManagedAppEndpointTransportApiV1 api,
        ulong endpointToken,
        ulong registrationToken,
        ulong generation,
        ulong operationToken) =>
        api.CancelOperation(endpointToken, registrationToken, generation, operationToken);

    private static unsafe int CallReplyTransportConfirmation(
        ManagedAppEndpointTransportApiV1 api,
        ulong endpointToken,
        TransportNativeRequest request,
        uint outcome,
        uint accepted,
        byte[] diagnostic)
    {
        fixed (byte* diagnosticPointer = diagnostic)
            return api.ReplyConfirmation(
                endpointToken,
                request.RegistrationToken,
                request.RegistrationGeneration,
                request.RequestToken,
                outcome,
                accepted,
                diagnosticPointer,
                checked((uint)diagnostic.Length));
    }

    private static unsafe int CallCancelTransportConfirmation(
        ManagedAppEndpointTransportApiV1 api,
        ulong endpointToken,
        ulong registrationToken,
        ulong generation,
        ulong requestToken) =>
        api.CancelConfirmation(endpointToken, registrationToken, generation, requestToken);

    private static unsafe int CallRevokeTransportEndpoint(
        ManagedAppEndpointTransportApiV1 api,
        ulong endpointToken) =>
        api.RevokeEndpoint(endpointToken);

    private static unsafe int CallGetTransportOutstandingWork(
        ManagedAppEndpointTransportApiV1 api,
        ulong endpointToken,
        out uint operations,
        out uint confirmations,
        out uint callbacks,
        out uint routeLeases)
    {
        operations = 0;
        confirmations = 0;
        callbacks = 0;
        routeLeases = 0;
        fixed (uint* operationsPointer = &operations)
        fixed (uint* confirmationsPointer = &confirmations)
        fixed (uint* callbacksPointer = &callbacks)
        fixed (uint* routeLeasesPointer = &routeLeases)
            return api.GetOutstandingWork(
                endpointToken,
                operationsPointer,
                confirmationsPointer,
                callbacksPointer,
                routeLeasesPointer);
    }

    private static unsafe int CallGetTransportOutstandingWorkWithNullOutputs(
        ManagedAppEndpointTransportApiV1 api,
        ulong endpointToken) =>
        api.GetOutstandingWork(endpointToken, null, null, null, null);

    private static int ReplyTransportConfirmation(
        ManagedAppEndpointTransportApiV1 api,
        ulong endpointToken,
        TransportNativeRequest request,
        bool accepted)
    {
        byte[] diagnostic = [];
        return CallReplyTransportConfirmation(
            api, endpointToken, request, (uint)LiftNativeReplyKind.Result,
            accepted ? 1u : 0u, diagnostic);
    }

    private static int CancelTransportConfirmation(
        ManagedAppEndpointTransportApiV1 api,
        ulong endpointToken,
        TransportNativeRequest request) =>
        CallCancelTransportConfirmation(
            api,
            endpointToken,
            request.RegistrationToken,
            request.RegistrationGeneration,
            request.RequestToken);

    private static byte[] GetTransportRequest(bool isNew, int resultCode) =>
        isNew
            ? AsyncCallbackV1SmokeSupport.EncodeOldRequest("approve", resultCode).ToArray()
            : AsyncCallbackV1SmokeSupport.EncodeNewRequest(
                "approve", resultCode, $"source-transport-{resultCode}").ToArray();

    private static void AssertTransportResult(
        TransportPublishedResult result,
        bool isNew,
        int expectedCode,
        bool accepted)
    {
        Require(result.Status == (uint)ManagedAppEndpointTransportStatusV1.Success &&
                result.Error.Length == 0,
            $"The source transport published a non-success result ({result.Status}).");
        if (isNew)
        {
            var decoded = AsyncCallbackV1SmokeSupport.DecodeNewResult(result.Result);
            Require(decoded.Code == expectedCode && decoded.Accepted == accepted &&
                    !decoded.Audited,
                "The source transport NEW module returned an unexpected generated Task<Result> result.");
        }
        else
        {
            var decoded = AsyncCallbackV1SmokeSupport.DecodeOldResult(result.Result);
            Require(decoded.Code == expectedCode && decoded.Accepted == accepted,
                "The source transport OLD module returned an unexpected generated Task<Result> result.");
        }
    }

    private static unsafe ManagedAppEndpointTransportApiV1 QueryManagedAppEndpointTransportApi()
    {
        var getApi = (delegate* unmanaged[Cdecl]<
            uint, uint, uint*, ManagedAppEndpointTransportApiV1*, int>)
            &ManagedAppEndpointTransportApiV1Exports.GetApiV1;
        uint requiredSize = 0;
        var queryStatus = getApi(
            ManagedAppEndpointTransportApiV1Exports.Version,
            0,
            &requiredSize,
            null);
        Require(queryStatus == (int)ManagedAppEndpointTransportStatusV1.Success &&
                requiredSize == sizeof(ManagedAppEndpointTransportApiV1),
            "The source transport API size query returned an invalid size or status.");

        var unsupported = default(ManagedAppEndpointTransportApiV1);
        new Span<byte>(&unsupported, sizeof(ManagedAppEndpointTransportApiV1)).Fill(0xA5);
        Require(getApi(
                    ManagedAppEndpointTransportApiV1Exports.Version + 1,
                    (uint)sizeof(ManagedAppEndpointTransportApiV1),
                    &requiredSize,
                    &unsupported) ==
                    (int)ManagedAppEndpointTransportStatusV1.Unsupported &&
                new ReadOnlySpan<byte>(&unsupported, sizeof(ManagedAppEndpointTransportApiV1))
                    .ToArray().All(value => value == 0xA5),
            "An unsupported source transport API version modified the output table.");

        Span<byte> shortBuffer = stackalloc byte[sizeof(ManagedAppEndpointTransportApiV1)];
        shortBuffer.Fill(0x5A);
        fixed (byte* shortPointer = shortBuffer)
        {
            Require(getApi(
                        ManagedAppEndpointTransportApiV1Exports.Version,
                        (uint)sizeof(ManagedAppEndpointTransportApiV1) - 1,
                        &requiredSize,
                        (ManagedAppEndpointTransportApiV1*)shortPointer) ==
                        (int)ManagedAppEndpointTransportStatusV1.BufferTooLarge &&
                    requiredSize == sizeof(ManagedAppEndpointTransportApiV1) &&
                    shortBuffer.ToArray().All(value => value == 0x5A),
                "A short source transport API table buffer was written.");
        }

        var api = default(ManagedAppEndpointTransportApiV1);
        var status = getApi(
            ManagedAppEndpointTransportApiV1Exports.Version,
            (uint)sizeof(ManagedAppEndpointTransportApiV1),
            &requiredSize,
            &api);
        Require(status == (int)ManagedAppEndpointTransportStatusV1.Success &&
                api.StructSize == sizeof(ManagedAppEndpointTransportApiV1) &&
                api.ApiVersion == ManagedAppEndpointTransportApiV1Exports.Version &&
                api.ConfigureNativeCallbacks is not null &&
                api.StartRequest is not null &&
                api.CancelOperation is not null &&
                api.ReplyConfirmation is not null &&
                api.CancelConfirmation is not null &&
                api.RevokeEndpoint is not null &&
                api.GetOutstandingWork is not null,
            "The source transport API table was incomplete or malformed.");
        return api;
    }

    private static unsafe nint GetNativeRequestCallbackPointer() =>
        (nint)(delegate* unmanaged[Cdecl]<
            ulong, ulong, ulong, ulong, uint, uint, byte*, uint, int>)
            &NativeRequestCallback;

    private static unsafe nint GetNativePublishResultCallbackPointer() =>
        (nint)(delegate* unmanaged[Cdecl]<
            ulong, ulong, ulong, uint, byte*, uint, byte*, uint, void>)
            &NativePublishResultCallback;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int NativeRequestCallback(
        ulong registrationToken,
        ulong registrationGeneration,
        ulong requestToken,
        ulong operationToken,
        uint serviceId,
        uint methodId,
        byte* payload,
        uint payloadLength)
    {
        if (payload is null || payloadLength == 0 ||
            !TransportRequests.TryGetValue(operationToken, out var completion))
            return 0;

        var request = new TransportNativeRequest(
            registrationToken,
            registrationGeneration,
            requestToken,
            operationToken,
            serviceId,
            methodId,
            new ReadOnlySpan<byte>(payload, checked((int)payloadLength)).ToArray(),
            GetEndpointTokenFromOperation(operationToken));
        completion.TrySetResult(request);
        var callbackRelease = Volatile.Read(ref _transportCallbackRelease);
        if (callbackRelease is not null &&
            !callbackRelease.Wait(TimeSpan.FromSeconds(10)))
            return 0;
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void NativePublishResultCallback(
        ulong registrationToken,
        ulong registrationGeneration,
        ulong operationToken,
        uint status,
        byte* result,
        uint resultLength,
        byte* error,
        uint errorLength)
    {
        if (!TransportResults.TryGetValue(operationToken, out var completion))
            return;
        var resultBytes = resultLength == 0
            ? []
            : new ReadOnlySpan<byte>(result, checked((int)resultLength)).ToArray();
        var errorBytes = errorLength == 0
            ? []
            : new ReadOnlySpan<byte>(error, checked((int)errorLength)).ToArray();
        completion.TrySetResult(new TransportPublishedResult(
            registrationToken, registrationGeneration, operationToken,
            status, resultBytes, errorBytes));
    }

    private static ulong GetEndpointTokenFromOperation(ulong operationToken) =>
        operationToken / 1000;

    private sealed record TransportNativeRequest(
        ulong RegistrationToken,
        ulong RegistrationGeneration,
        ulong RequestToken,
        ulong OperationToken,
        uint ServiceId,
        uint MethodId,
        byte[] Payload,
        ulong EndpointToken);

    private sealed record TransportPublishedResult(
        ulong RegistrationToken,
        ulong RegistrationGeneration,
        ulong OperationToken,
        uint Status,
        byte[] Result,
        byte[] Error);

    private sealed record PendingTransportOperation(
        ulong OperationToken,
        TaskCompletionSource<TransportNativeRequest> Request,
        TaskCompletionSource<TransportPublishedResult> Result);

    public static Task RunAsync(bool isNew, string moduleAssemblyPath) =>
        RunCoreAsync(isNew, moduleAssemblyPath, null, ProviderTypeName, null);

    internal static Task RunCapturedAsync(
        bool isNew,
        CapturedManagedAppModule capturedModule,
        string providerTypeName,
        string registrationCase) =>
        RunCoreAsync(isNew, null, capturedModule, providerTypeName, registrationCase);

    private static async Task RunCoreAsync(
        bool isNew,
        string? moduleAssemblyPath,
        CapturedManagedAppModule? capturedModule,
        string providerTypeName,
        string? registrationCase)
    {
        ILiveEndpointApi localEndpoint = isNew ? new NewEndpointApi() : new OldEndpointApi();
        ILiveEndpointApi peerEndpoint = isNew ? new OldEndpointApi() : new NewEndpointApi();
        var localManifest = localEndpoint.GetManifest();
        var peerManifest = peerEndpoint.GetManifest();
        var localContract = NegotiationManifest.LoadEmbedded(localManifest);
        var peerContract = NegotiationManifest.LoadEmbedded(peerManifest);
        var peerHello = CompatibleStartPeerHelloV1.Encode(peerContract);
        var trustedPeerHash = Convert.FromHexString(peerContract.SchemaHash);
        var endpointToken = localEndpoint.CreateEndpoint();

        localEndpoint.Start(endpointToken, peerManifest, peerHello, trustedPeerHash);
        var peerSchema = localEndpoint.GetPeerRequestSchema(endpointToken);
        var managedRoute = ManagedAppEndpointDescriptorFactory.GetManagedRouteIds(localManifest);
        var resultSchema = localEndpoint.GetManagedResultSchema(
            endpointToken, managedRoute.ServiceId, managedRoute.MethodId);
        var descriptor = ManagedAppEndpointDescriptorFactory.Create(
            endpointToken, localManifest, peerSchema, resultSchema);
        var provider = ManagedAppHostApiV1Exports.ConfiguredBindingProvider
            as ManagedAppEndpointBindingProvider
            ?? throw new InvalidOperationException(
                "The endpoint's NativeApi.Start publisher did not configure the source-owned provider.");
        if (capturedModule is not null)
            Require(ReferenceEquals(
                    provider, ManagedAppEndpointAuthority.Process.BindingProvider),
                "Captured registration did not use the source-owned process authority binding provider.");

        Require(!CryptographicOperations.FixedTimeEquals(
                descriptor.LocalSchemaHash.Span, descriptor.PeerSchemaHash.Span),
            "The live endpoint smoke did not negotiate distinct OLD/NEW schema pins.");
        using (var binding = provider.Acquire(endpointToken))
        {
            Require(binding is not null &&
                    binding.Descriptor.PeerRequestSchema.Span.SequenceEqual(
                        descriptor.PeerRequestSchema.Span) &&
                    binding.Descriptor.ManagedResultSchema.Span.SequenceEqual(
                        descriptor.ManagedResultSchema.Span),
                "The provider was not populated from the endpoint's own schema getters.");
        }
        Require(provider.Publish(descriptor) == ManagedAppEndpointBindingStatus.DuplicateEndpoint,
            "The provider accepted a duplicate endpoint publication.");
        Require(provider.ActiveEndpointCount == 1 &&
                provider.OutstandingBindingLeaseCount == 0 &&
                provider.OutstandingResultContextLeaseCount == 0,
            "Publishing a live endpoint unexpectedly acquired a lease.");

        var directApi = QueryManagedAppHostApi();
        var api = QueryForwardedManagedAppHostApi(isNew);
        Require(AreSameApiTables(api, directApi),
            "The same-component GetApiV1 forwarder returned a different ManagedHost function table.");
        var tamperedPeerSchema = descriptor.PeerRequestSchema.ToArray();
        tamperedPeerSchema[^1] ^= 1;
        var rejectedStatus = CallRegisterForModule(
            api,
            endpointToken,
            tamperedPeerSchema,
            descriptor.ManagedResultSchema.ToArray(),
            localManifest,
            moduleAssemblyPath,
            capturedModule,
            providerTypeName,
            out var rejectedLease);
        Require(rejectedStatus == (int)ManagedAppHostStatusV1.SchemaMismatch &&
                rejectedLease == 0 &&
                provider.RegisteredRouteCount == 0 &&
                provider.OutstandingBindingLeaseCount == 0 &&
                ManagedAppHostApiV1Exports.OutstandingLeaseCount == 0,
            "A C registration snapshot mismatch published a route or retained a lease.");

        if (capturedModule is not null &&
            registrationCase is not null &&
            registrationCase != "positive")
        {
            await RunCapturedRegistrationNegativeAsync(
                registrationCase,
                localEndpoint,
                endpointToken,
                localManifest,
                descriptor,
                provider,
                api,
                capturedModule,
                providerTypeName).ConfigureAwait(false);
            return;
        }

        var status = CallRegisterForModule(
            api,
            endpointToken,
            descriptor.PeerRequestSchema.ToArray(),
            descriptor.ManagedResultSchema.ToArray(),
            localManifest,
            moduleAssemblyPath,
            capturedModule,
            providerTypeName,
            out var leaseId);
        Require(status == (int)ManagedAppHostStatusV1.Success &&
                leaseId != 0 &&
                provider.RegisteredRouteCount == 1 &&
                provider.OutstandingBindingLeaseCount == 1 &&
                provider.OutstandingResultContextLeaseCount == 1,
            $"Live OLD/NEW module route registration failed with status {status}.");

#if NETLIFT_MANAGED_HOST_DISPATCH_SMOKE
        var registrationToken = checked(endpointToken + 2);
        const ulong registrationGeneration = 1;
        var operationToken = checked(endpointToken + 3);
        _expectedRegistrationToken = registrationToken;
        _expectedRegistrationGeneration = registrationGeneration;
        _expectedOperationToken = operationToken;
        _expectedNativeServiceId = descriptor.NativeServiceId;
        _expectedNativeMethodId = descriptor.NativeMethodId;
        _nativeRequestSeen = new TaskCompletionSource<ulong>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _publishedResult = new TaskCompletionSource<PublishedResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Require(ConfigureNativeCallbacks(isNew) == Ok,
            "The fixture async runtime rejected native callback configuration.");
        var request = isNew
            ? AsyncCallbackV1SmokeSupport.EncodeOldRequest("approve", 17).ToArray()
            : AsyncCallbackV1SmokeSupport.EncodeNewRequest("approve", 17, string.Empty).ToArray();
        Require(CallStartRequest(
                    isNew,
                    endpointToken,
                    registrationToken,
                    registrationGeneration,
                    operationToken,
                    request) == Ok,
            "StartRequestV2 did not dispatch the API-V1 registered module route.");
        var requestToken = await _nativeRequestSeen.Task
            .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Require(requestToken != 0 &&
                provider.ActiveRouteLeaseCount == 1,
            "The managed dispatch did not hold a provider route lease through its typed callback.");

        var drainingCloseStatus = CallClose(api, leaseId);
        Require(drainingCloseStatus == (int)ManagedAppHostStatusV1.Busy &&
                provider.RegisteredRouteCount == 1 &&
                provider.ActiveRouteLeaseCount == 1 &&
                provider.OutstandingBindingLeaseCount == 2 &&
                provider.OutstandingResultContextLeaseCount == 2,
            $"Close did not stop admissions and return Busy while retaining the active route/module lease " +
            $"(status={drainingCloseStatus}, routes={provider.RegisteredRouteCount}, " +
            $"active={provider.ActiveRouteLeaseCount}, bindings={provider.OutstandingBindingLeaseCount}, " +
            $"results={provider.OutstandingResultContextLeaseCount}).");
        Require(CallStartRequest(
                    isNew,
                    endpointToken,
                    registrationToken,
                    registrationGeneration,
                    operationToken + 1,
                    request) == StaleHandle,
            "A route remained admissible after API-V1 close began draining it.");

        Require(CallReplyConfirmation(
                    isNew,
                    registrationToken,
                    registrationGeneration,
                    requestToken) == Ok,
            "The fixture could not return its typed confirmation.");
        var published = await _publishedResult.Task
            .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Require(published.Status == Success && published.Result.Length != 0,
            $"The generated API-V1 route did not publish a result (status {published.Status}).");
        var decodeStatus = CallDecodeResult(
            isNew,
            endpointToken,
            descriptor.ManagedResultSchema.ToArray(),
            published.Result,
            out var code,
            out var accepted,
            out var audited);
        Require(decodeStatus == Ok && code == 17 && accepted == 1 && audited == 0,
            $"The endpoint-descriptor-backed result decoder returned an unexpected result " +
            $"(status={decodeStatus}, code={code}, accepted={accepted}, audited={audited}).");
        Require(CallOutstandingManagedWork(isNew) == 0 &&
                provider.ActiveRouteLeaseCount == 0,
            "The async dispatch or provider route lease remained active after result publication.");
        _nativeRequestSeen = null;
        _publishedResult = null;

        Require(CallClose(api, leaseId) == (int)ManagedAppHostStatusV1.Success &&
                provider.RegisteredRouteCount == 0,
            "The API-V1 lease did not close after provider route drain.");
        AssertCapturedRegistrationOneShot(
            capturedModule,
            endpointToken,
            descriptor,
            localManifest,
            providerTypeName,
            provider);
        Require(CallInvalidateManagedResultContext(isNew, endpointToken) == Ok,
            "The fixture result-field context did not clear without invalidating its endpoint binding.");
        Require(provider.Invalidate(endpointToken) == ManagedAppEndpointBindingStatus.Success &&
                provider.ActiveEndpointCount == 0 &&
                provider.OutstandingBindingLeaseCount == 0 &&
                provider.OutstandingResultContextLeaseCount == 0 &&
                provider.RegisteredRouteCount == 0 &&
                provider.Acquire(endpointToken) is null &&
                provider.Publish(descriptor) == ManagedAppEndpointBindingStatus.DuplicateEndpoint &&
                ManagedAppHostApiV1Exports.OutstandingLeaseCount == 0 &&
                GeneratedHostRegistration.OutstandingRegistrationCount == 0,
            "Successful drain did not clear all leases while retaining the stale-token tombstone.");
#else
        Require(provider.TryAcquireRegisteredRoute(
                endpointToken, out var endpointDescriptor, out var handler, out var routeLease) &&
                routeLease is not null &&
                endpointDescriptor.ManagedResultSchema.Span.SequenceEqual(
                    descriptor.ManagedResultSchema.Span),
            "The source-owned registry did not expose the generated managed route and endpoint descriptor.");
        var activeRouteLease = routeLease
            ?? throw new InvalidOperationException("A registered route had no lease.");
        var cancellation = new CancellationTokenSource();
        var invoker = new AsyncCallbackV1SmokeSupport.AsyncCallbackV1SmokeInvoker(
            confirmationAccepted: true);
        var invocationContext = new LiftInvocationContext(
            cancellation.Token,
            checked(endpointToken + 1),
            invoker)
        {
            EndpointToken = endpointToken,
            RegistrationToken = checked(endpointToken + 2),
            RegistrationGeneration = 1,
            OperationToken = checked(endpointToken + 3),
            PeerRequestSchema = endpointDescriptor.PeerRequestSchema,
            ManagedResultSchema = endpointDescriptor.ManagedResultSchema
        };
        var request = isNew
            ? AsyncCallbackV1SmokeSupport.EncodeOldRequest("approve", 17)
            : AsyncCallbackV1SmokeSupport.EncodeNewRequest("approve", 17, string.Empty);
        var encodedResult = await handler(
            request,
            invocationContext,
            cancellation.Token).ConfigureAwait(false);
        if (isNew)
        {
            var result = AsyncCallbackV1SmokeSupport.DecodeNewResult(encodedResult);
            Require(result.Code == 17 && result.Accepted && !result.Audited,
                "The live NEW generated route returned an unexpected result.");
        }
        else
        {
            var result = AsyncCallbackV1SmokeSupport.DecodeOldResult(encodedResult);
            Require(result.Code == 17 && result.Accepted,
                "The live OLD generated route returned an unexpected result.");
        }
        Require(invoker.CallCount == 1 &&
                invoker.LastServiceId == descriptor.NativeServiceId &&
                invoker.LastMethodId == descriptor.NativeMethodId,
            "The live generated route did not make its typed native callback.");
        cancellation.Dispose();

        Require(provider.Invalidate(endpointToken) == ManagedAppEndpointBindingStatus.Busy &&
                provider.ActiveRouteLeaseCount == 1,
            "Endpoint invalidation did not refuse while a provider route lease was active.");
        Require(CallClose(api, leaseId) == (int)ManagedAppHostStatusV1.Busy &&
                provider.RegisteredRouteCount == 1 &&
                provider.ActiveRouteLeaseCount == 1,
            "API-V1 close did not return Busy while a provider route lease was active.");
        activeRouteLease.Dispose();
        Require(provider.ActiveRouteLeaseCount == 0 &&
                CallClose(api, leaseId) == (int)ManagedAppHostStatusV1.Success &&
                provider.RegisteredRouteCount == 0,
            "API-V1 close did not succeed after provider route drain.");
        Require(provider.Invalidate(endpointToken) == ManagedAppEndpointBindingStatus.Success &&
                provider.ActiveEndpointCount == 0 &&
                provider.OutstandingBindingLeaseCount == 0 &&
                provider.OutstandingResultContextLeaseCount == 0 &&
                provider.RegisteredRouteCount == 0 &&
                provider.Acquire(endpointToken) is null &&
                provider.Publish(descriptor) == ManagedAppEndpointBindingStatus.DuplicateEndpoint &&
                ManagedAppHostApiV1Exports.OutstandingLeaseCount == 0 &&
                GeneratedHostRegistration.OutstandingRegistrationCount == 0,
            "Successful drain did not clear all leases while retaining the stale-token tombstone.");
#endif
        Require(localEndpoint.CloseEndpoint(endpointToken) == Success,
            "The live endpoint host refused close after binding drain.");

        await AssertStartClosePublicationRaceAsync(
            localEndpoint, peerManifest, peerHello, trustedPeerHash, provider);
        AssertFailedPublicationRollsBackEndpoint(
            localEndpoint, peerManifest, peerHello, trustedPeerHash, managedRoute, provider);

#if NETLIFT_MANAGED_HOST_DISPATCH_SMOKE
        Console.WriteLine(
            $"PASS: live {(isNew ? "NEW" : "OLD")} endpoint binding, cross-hash generated route, " +
                $"{(capturedModule is null ? "path-based" : "captured-byte")} registration, " +
                "descriptor-backed StartRequestV2 dispatch, " +
                "typed confirmation/result, Busy route drain, descriptor mismatch, stale/duplicate token, " +
                "Start/close race serialization, publication-failure rollback, and zero leases.");
#else
        Console.WriteLine(
            $"PASS: live {(isNew ? "NEW" : "OLD")} endpoint binding, cross-hash generated route, " +
                $"{(capturedModule is null ? "path-based" : "captured-byte")} registration, " +
                "direct typed route callback, Busy route drain, descriptor mismatch, " +
                "stale/duplicate token, Start/close race serialization, publication-failure rollback, and zero leases.");
#endif
    }

    private static Task RunCapturedRegistrationNegativeAsync(
        string registrationCase,
        ILiveEndpointApi localEndpoint,
        ulong endpointToken,
        byte[] localManifest,
        LiftAsyncCallbackV1HostDescriptor descriptor,
        ManagedAppEndpointBindingProvider provider,
        ManagedAppHostApiV1 api,
        CapturedManagedAppModule capturedModule,
        string providerTypeName)
    {
        var manifestForRegistration = registrationCase == "schema-mismatch"
            ? localManifest.Append((byte)' ').ToArray()
            : localManifest;
        var failedLoadsBefore = ManagedAppModuleLease.FailedLoadRootCount;
        var status = RegisterCaptured(
            endpointToken,
            descriptor.PeerRequestSchema,
            descriptor.ManagedResultSchema,
            manifestForRegistration,
            capturedModule,
            providerTypeName,
            out var leaseId);

        switch (registrationCase)
        {
            case "wrong-digest":
                Require(status == (int)ManagedAppHostStatusV1.SchemaMismatch &&
                        leaseId == 0 &&
                        !ManagedAppModuleLease.ModuleLoadAttempted &&
                        ManagedAppModuleLease.FailedLoadRootCount == failedLoadsBefore,
                    "A captured root digest mismatch executed or retained a module.");
                break;
            case "missing-dependency":
                Require(status == (int)ManagedAppHostStatusV1.ModuleUnavailable &&
                        leaseId == 0 &&
                        !ManagedAppModuleLease.ModuleLoadAttempted &&
                        ManagedAppModuleLease.FailedLoadRootCount == failedLoadsBefore,
                    "A missing captured dependency executed or retained a module.");
                break;
            case "native-rejected":
                Require(status == (int)ManagedAppHostStatusV1.ManagedFailure &&
                        leaseId == 0 &&
                        !ManagedAppModuleLease.ModuleLoadAttempted &&
                        ManagedAppModuleLease.FailedLoadRootCount == failedLoadsBefore,
                    "A captured P/Invoke module was not rejected before execution.");
                break;
            case "schema-mismatch":
                Require(status == (int)ManagedAppHostStatusV1.SchemaMismatch &&
                        leaseId != 0 &&
                        ManagedAppModuleLease.ModuleLoadAttempted &&
                        GeneratedHostRegistration.OutstandingRegistrationCount == 0,
                    "A post-load canonical-manifest mismatch did not retain the loaded module lease.");
                Require(CallClose(api, leaseId) == (int)ManagedAppHostStatusV1.Success,
                    "The retained schema-mismatch module lease did not close.");
                break;
            case "quarantine":
                Require(status == (int)ManagedAppHostStatusV1.ManagedFailure &&
                        leaseId != 0 &&
                        ManagedAppModuleLease.FailedLoadRootCount == failedLoadsBefore + 1,
                    "A provider-construction failure did not retain its quarantined load root.");
                Require(CallClose(api, leaseId) == (int)ManagedAppHostStatusV1.ManagedFailure &&
                        ManagedAppHostApiV1Exports.OutstandingLeaseCount == 1 &&
                        provider.OutstandingBindingLeaseCount == 1,
                    "A quarantined captured load was released instead of remaining process-pinned.");
                Console.WriteLine(
                    "PASS: captured provider failure retains permanent quarantine; close remains fail-closed.");
                return Task.CompletedTask;
            default:
                throw new ArgumentException(
                    $"Unknown captured registration case '{registrationCase}'.");
        }

        Require(ManagedAppHostApiV1Exports.OutstandingLeaseCount == 0 &&
                GeneratedHostRegistration.OutstandingRegistrationCount == 0 &&
                provider.OutstandingBindingLeaseCount == 0 &&
                provider.OutstandingResultContextLeaseCount == 0 &&
                provider.RegisteredRouteCount == 0,
            $"Captured registration case '{registrationCase}' leaked a lease.");
        Require(localEndpoint.CloseEndpoint(endpointToken) == Success,
            $"The source authority did not close after '{registrationCase}'.");
        Console.WriteLine(
            $"PASS: captured registration '{registrationCase}' status {status}; clean close left zero leases.");
        return Task.CompletedTask;
    }

    private static void AssertCapturedRegistrationOneShot(
        CapturedManagedAppModule? capturedModule,
        ulong endpointToken,
        LiftAsyncCallbackV1HostDescriptor descriptor,
        byte[] localManifest,
        string providerTypeName,
        ManagedAppEndpointBindingProvider provider)
    {
        if (capturedModule is null)
            return;

        var status = RegisterCaptured(
            endpointToken,
            descriptor.PeerRequestSchema,
            descriptor.ManagedResultSchema,
            localManifest,
            capturedModule,
            providerTypeName,
            out var leaseId);
        Require(status == (int)ManagedAppHostStatusV1.ManagedFailure &&
                leaseId == 0 &&
                ManagedAppHostApiV1Exports.OutstandingLeaseCount == 0 &&
                provider.OutstandingBindingLeaseCount == 0 &&
                GeneratedHostRegistration.OutstandingRegistrationCount == 0,
            "A second captured module registration was accepted or retained a lease.");
    }

    private static int CallRegisterForModule(
            ManagedAppHostApiV1 api,
            ulong endpointToken,
            byte[] nlps,
            byte[] nlmr,
            byte[] localManifest,
            string? modulePath,
            CapturedManagedAppModule? capturedModule,
            string providerTypeName,
            out ulong leaseId)
    {
            if (capturedModule is not null)
                return RegisterCaptured(
                    endpointToken,
                    nlps,
                    nlmr,
                    localManifest,
                    capturedModule,
                    providerTypeName,
                    out leaseId);

            return CallRegister(
                api,
                endpointToken,
                nlps,
                nlmr,
                localManifest,
                modulePath ?? throw new InvalidOperationException(
                    "Path registration requires a module assembly path."),
                providerTypeName,
                out leaseId);
    }

    private static int RegisterCaptured(
            ulong endpointToken,
            ReadOnlyMemory<byte> nlps,
            ReadOnlyMemory<byte> nlmr,
            ReadOnlyMemory<byte> localManifest,
            CapturedManagedAppModule capturedModule,
            string providerTypeName,
            out ulong leaseId) =>
            ManagedAppHostApiV1Exports.RegisterGeneratedHostFromCaptured(
                endpointToken,
                nlps,
                nlmr,
                localManifest,
                capturedModule,
                providerTypeName,
                out leaseId);

    private static async Task AssertStartClosePublicationRaceAsync(
        ILiveEndpointApi localEndpoint,
        byte[] peerManifest,
        byte[] peerHello,
        byte[] trustedPeerHash,
        ManagedAppEndpointBindingProvider provider)
    {
        var endpointToken = localEndpoint.CreateEndpoint();
        using var publisherEntered = new ManualResetEventSlim();
        using var allowPublish = new ManualResetEventSlim();
        using var closeAttempted = new ManualResetEventSlim();
        using var closeEntered = new ManualResetEventSlim();
        localEndpoint.SetTestingHooks(
            () =>
            {
                publisherEntered.Set();
                if (!allowPublish.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Timed out waiting to resume the test publisher.");
            },
            closeAttempted.Set,
            closeEntered.Set);

        Task? startTask = null;
        Task<uint>? closeTask = null;
        try
        {
            startTask = Task.Run(() =>
                localEndpoint.Start(endpointToken, peerManifest, peerHello, trustedPeerHash));
            Require(publisherEntered.Wait(TimeSpan.FromSeconds(5)),
                "Start did not reach the deterministic pre-publication pause.");

            closeTask = Task.Run(() => localEndpoint.CloseEndpoint(endpointToken));
            Require(closeAttempted.Wait(TimeSpan.FromSeconds(5)),
                "Close did not reach the endpoint gate.");
            Require(!closeEntered.Wait(TimeSpan.FromMilliseconds(150)),
                "Close acquired the endpoint gate while Start was publishing its binding.");

            allowPublish.Set();
            await startTask.ConfigureAwait(false);
            Require(closeEntered.Wait(TimeSpan.FromSeconds(5)),
                "Close did not acquire the endpoint gate after Start publication completed.");
            Require(await closeTask.ConfigureAwait(false) == Success,
                "Close failed after the serialized Start publication completed.");
            Require(provider.Acquire(endpointToken) is null &&
                    provider.ActiveEndpointCount == 0 &&
                    provider.OutstandingBindingLeaseCount == 0 &&
                    provider.OutstandingResultContextLeaseCount == 0,
                "A binding survived the serialized Start/close race.");
        }
        finally
        {
            allowPublish.Set();
            if (startTask is not null)
                await startTask.ConfigureAwait(false);
            if (closeTask is not null)
                await closeTask.ConfigureAwait(false);
            localEndpoint.SetTestingHooks(null, null, null);
        }
    }

    private static void AssertFailedPublicationRollsBackEndpoint(
        ILiveEndpointApi localEndpoint,
        byte[] peerManifest,
        byte[] peerHello,
        byte[] trustedPeerHash,
        (uint ServiceId, uint MethodId) managedRoute,
        ManagedAppEndpointBindingProvider provider)
    {
        var endpointToken = localEndpoint.CreateEndpoint();
        localEndpoint.SetTestingHooks(
            () => throw new InvalidOperationException("Injected binding publication failure."),
            null,
            null);
        try
        {
            var startFailure = CaptureException(() =>
                localEndpoint.Start(endpointToken, peerManifest, peerHello, trustedPeerHash));
            Require(startFailure is InvalidOperationException &&
                    startFailure.Message.Contains("11/11", StringComparison.Ordinal),
                "An injected publication failure did not fail endpoint Start.");
            Require(provider.Acquire(endpointToken) is null &&
                    provider.ActiveEndpointCount == 0,
                "A failed Start left a source-owned endpoint binding.");

            var peerSchemaFailure = CaptureException(
                () => localEndpoint.GetPeerRequestSchema(endpointToken));
            var resultSchemaFailure = CaptureException(
                () => localEndpoint.GetManagedResultSchema(
                    endpointToken, managedRoute.ServiceId, managedRoute.MethodId));
            Require(peerSchemaFailure is InvalidOperationException &&
                    peerSchemaFailure.Message.Contains("9/9", StringComparison.Ordinal) &&
                    resultSchemaFailure is InvalidOperationException &&
                    resultSchemaFailure.Message.Contains("status 9", StringComparison.Ordinal),
                "A failed Start left negotiated peer or result schema visible.");
            Require(localEndpoint.CloseEndpoint(endpointToken) == Success,
                "Close could not retire an endpoint after publication rollback.");
            Require(provider.ActiveEndpointCount == 0 &&
                    provider.OutstandingBindingLeaseCount == 0 &&
                    provider.OutstandingResultContextLeaseCount == 0,
                "Publication rollback leaked endpoint or result-context leases.");
        }
        finally
        {
            localEndpoint.SetTestingHooks(null, null, null);
        }
    }

    private static Exception? CaptureException(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static unsafe ManagedAppEndpointAuthorityApiV1 QueryManagedAppEndpointAuthorityApi()
    {
        var getApi = (delegate* unmanaged[Cdecl]<
            uint, uint, uint*, ManagedAppEndpointAuthorityApiV1*, int>)
            &ManagedAppEndpointAuthorityApiV1Exports.GetApiV1;
        uint requiredSize = 0;
        var queryStatus = getApi(
            ManagedAppEndpointAuthorityApiV1Exports.Version, 0, &requiredSize, null);
        Require(queryStatus == (int)ManagedAppEndpointAuthorityStatus.Success &&
                requiredSize == sizeof(ManagedAppEndpointAuthorityApiV1) &&
                requiredSize == 2 * sizeof(uint) + 5 * IntPtr.Size,
            "The Cdecl endpoint-authority size query returned an unexpected size or status.");

        var shortTable = new ManagedAppEndpointAuthorityApiV1
        {
            StructSize = 0x12345678,
            ApiVersion = 0x87654321
        };
        var shortStatus = getApi(
            ManagedAppEndpointAuthorityApiV1Exports.Version,
            requiredSize - 1,
            &requiredSize,
            &shortTable);
        Require(shortStatus == (int)ManagedAppEndpointAuthorityStatus.BufferTooSmall &&
                requiredSize == sizeof(ManagedAppEndpointAuthorityApiV1) &&
                shortTable.StructSize == 0x12345678 &&
                shortTable.ApiVersion == 0x87654321,
            "The Cdecl endpoint-authority short-table query wrote output or returned the wrong status.");

        var unsupportedTable = shortTable;
        var unsupportedStatus = getApi(
            ManagedAppEndpointAuthorityApiV1Exports.Version + 1,
            (uint)sizeof(ManagedAppEndpointAuthorityApiV1),
            &requiredSize,
            &unsupportedTable);
        Require(unsupportedStatus == (int)ManagedAppEndpointAuthorityStatus.UnsupportedVersion &&
                unsupportedTable.StructSize == 0x12345678 &&
                unsupportedTable.ApiVersion == 0x87654321,
            "The Cdecl endpoint-authority unsupported-version query wrote output.");

        var status = getApi(
            ManagedAppEndpointAuthorityApiV1Exports.Version,
            (uint)sizeof(ManagedAppEndpointAuthorityApiV1),
            &requiredSize,
            &shortTable);
        Require(status == (int)ManagedAppEndpointAuthorityStatus.Success &&
                shortTable.StructSize == sizeof(ManagedAppEndpointAuthorityApiV1) &&
                shortTable.ApiVersion == ManagedAppEndpointAuthorityApiV1Exports.Version &&
                shortTable.CreateEndpoint is not null &&
                shortTable.StartEndpoint is not null &&
                shortTable.GetSnapshot is not null &&
                shortTable.CloseEndpoint is not null &&
                shortTable.FreeBuffer is not null,
            "The Cdecl endpoint-authority query returned an invalid function table.");
        return shortTable;
    }

    private static unsafe void TestAuthorityApiBadInputs(
        ManagedAppEndpointAuthorityApiV1 api,
        byte[] localManifest,
        byte[] peerManifest,
        byte[] peerHello,
        byte[] trustedPeerSchemaHash,
        uint minimumPeerContractMajor,
        uint minimumPeerContractMinor)
    {
        ulong endpointToken = ulong.MaxValue;
        var tooLargeStatus = api.CreateEndpoint(
            (byte*)1,
            (uint)ManagedAppEndpointAuthorityApiV1Exports.MaximumManifestBytes + 1,
            1,
            (byte*)1,
            32,
            1,
            0,
            &endpointToken);
        Require(tooLargeStatus == (int)ManagedAppEndpointAuthorityStatus.InvalidArgument &&
                endpointToken == 0,
            "Cdecl Create did not reject an oversized borrowed manifest and clear the token.");

        endpointToken = ulong.MaxValue;
        var invalidBooleanStatus = api.CreateEndpoint(
            (byte*)1, 1, 2, (byte*)1, 32, 1, 0, &endpointToken);
        Require(invalidBooleanStatus == (int)ManagedAppEndpointAuthorityStatus.InvalidArgument &&
                endpointToken == 0,
            "Cdecl Create accepted a non-boolean opt-in value.");
        fixed (byte* localPointer = localManifest)
        fixed (byte* hashPointer = trustedPeerSchemaHash)
        fixed (byte* peerManifestPointer = peerManifest)
        fixed (byte* peerHelloPointer = peerHello)
        {
            endpointToken = ulong.MaxValue;
            Require(api.CreateEndpoint(
                        localPointer,
                        (uint)localManifest.Length,
                        1,
                        hashPointer,
                        31,
                        minimumPeerContractMajor,
                        minimumPeerContractMinor,
                        &endpointToken) ==
                    (int)ManagedAppEndpointAuthorityStatus.InvalidArgument &&
                    endpointToken == 0 &&
                    api.StartEndpoint(
                        1,
                        peerManifestPointer,
                        (uint)peerManifest.Length,
                        peerHelloPointer,
                        (uint)peerHello.Length,
                        2) == (int)ManagedAppEndpointAuthorityStatus.InvalidArgument,
                "Cdecl Create accepted a non-SHA256 pin or Start accepted a non-boolean opt-in.");
        }

        byte* outputData = (byte*)1;
        uint outputLength = uint.MaxValue;
        Require(api.GetSnapshot(1, 1, null, &outputLength) ==
                    (int)ManagedAppEndpointAuthorityStatus.InvalidArgument &&
                outputLength == 0,
            "Cdecl GetSnapshot did not validate a null data-output pointer.");
        outputData = (byte*)1;
        Require(api.GetSnapshot(1, 1, &outputData, null) ==
                    (int)ManagedAppEndpointAuthorityStatus.InvalidArgument &&
                outputData is null &&
                AuthoritySnapshotStatus(api, 0, uint.MaxValue) ==
                    (int)ManagedAppEndpointAuthorityStatus.InvalidArgument &&
                api.CreateEndpoint(null, 0, 0, null, 0, 0, 0, null) ==
                    (int)ManagedAppEndpointAuthorityStatus.InvalidArgument &&
                api.StartEndpoint(1, null, 0, null, 0, 0) ==
                    (int)ManagedAppEndpointAuthorityStatus.InvalidArgument &&
                api.FreeBuffer(null) == (int)ManagedAppEndpointAuthorityStatus.InvalidArgument &&
                api.FreeBuffer((byte*)1) == (int)ManagedAppEndpointAuthorityStatus.InvalidArgument &&
                ManagedAppEndpointAuthorityApiV1Exports.OutstandingBufferCount == 0 &&
                ManagedAppEndpointAuthority.Process.EndpointCount == 0,
            "Cdecl authority bad-buffer validation returned an invalid status or retained a buffer.");
    }

    private static unsafe int CallAuthorityCreate(
        ManagedAppEndpointAuthorityApiV1 api,
        byte[] localManifest,
        uint localCompatibleStartOptIn,
        byte[] trustedPeerSchemaHash,
        uint minimumPeerContractMajor,
        uint minimumPeerContractMinor,
        out ulong endpointToken)
    {
        ulong returnedToken = ulong.MaxValue;
        fixed (byte* localPointer = localManifest)
        fixed (byte* hashPointer = trustedPeerSchemaHash)
        {
            var status = api.CreateEndpoint(
                localPointer,
                (uint)localManifest.Length,
                localCompatibleStartOptIn,
                hashPointer,
                (uint)trustedPeerSchemaHash.Length,
                minimumPeerContractMajor,
                minimumPeerContractMinor,
                &returnedToken);
            endpointToken = returnedToken;
            return status;
        }
    }

    private static unsafe int CallAuthorityStart(
        ManagedAppEndpointAuthorityApiV1 api,
        ulong endpointToken,
        byte[] peerManifest,
        byte[] peerHello,
        uint peerCompatibleStartOptIn)
    {
        fixed (byte* manifestPointer = peerManifest)
        fixed (byte* helloPointer = peerHello)
        {
            return api.StartEndpoint(
                endpointToken,
                manifestPointer,
                (uint)peerManifest.Length,
                helloPointer,
                (uint)peerHello.Length,
                peerCompatibleStartOptIn);
        }
    }

    private static unsafe int CallAuthorityClose(
        ManagedAppEndpointAuthorityApiV1 api,
        ulong endpointToken) =>
        api.CloseEndpoint(endpointToken);

    private static unsafe int AuthoritySnapshotStatus(
        ManagedAppEndpointAuthorityApiV1 api,
        ulong endpointToken,
        uint snapshotKind)
    {
        byte* outputData = (byte*)1;
        uint outputLength = uint.MaxValue;
        var status = api.GetSnapshot(
            endpointToken, snapshotKind, &outputData, &outputLength);
        Require(outputData is null && outputLength == 0,
            "Cdecl GetSnapshot did not clear outputs on failure.");
        return status;
    }

    private static unsafe byte[] CopyAuthoritySnapshot(
        ManagedAppEndpointAuthorityApiV1 api,
        ulong endpointToken,
        uint snapshotKind)
    {
        byte* outputData = null;
        uint outputLength = 0;
        var status = api.GetSnapshot(
            endpointToken, snapshotKind, &outputData, &outputLength);
        Require(status == (int)ManagedAppEndpointAuthorityStatus.Success &&
                outputData is not null &&
                outputLength != 0 &&
                outputLength <= ManagedAppEndpointAuthorityApiV1Exports.MaximumManifestBytes,
            $"Cdecl GetSnapshot failed for kind {snapshotKind} ({status}).");

        var result = new ReadOnlySpan<byte>(
            outputData, checked((int)outputLength)).ToArray();
        Require(api.FreeBuffer(outputData) ==
                    (int)ManagedAppEndpointAuthorityStatus.Success &&
                api.FreeBuffer(outputData) ==
                    (int)ManagedAppEndpointAuthorityStatus.InvalidArgument,
            "Cdecl FreeBuffer did not accept one ownership release and reject its duplicate.");
        return result;
    }

    private static unsafe ManagedAppHostApiV1 QueryManagedAppHostApi()
    {
        var getApi = (delegate* unmanaged[Cdecl]<
            uint, uint, uint*, ManagedAppHostApiV1*, int>)&ManagedAppHostApiV1Exports.GetApiV1;
        uint requiredSize = 0;
        var queryStatus = getApi(ManagedAppHostApiV1Exports.Version, 0, &requiredSize, null);
        Require(queryStatus == (int)ManagedAppHostStatusV1.Success &&
                requiredSize == sizeof(ManagedAppHostApiV1),
            "ManagedAppHostApiV1 size query returned an invalid size or status.");

        var api = default(ManagedAppHostApiV1);
        var status = getApi(
            ManagedAppHostApiV1Exports.Version,
            (uint)sizeof(ManagedAppHostApiV1),
            &requiredSize,
            &api);
        Require(status == (int)ManagedAppHostStatusV1.Success &&
                api.StructSize == sizeof(ManagedAppHostApiV1) &&
                api.ApiVersion == ManagedAppHostApiV1Exports.Version &&
                api.RegisterGeneratedHostFromPath is not null &&
                api.CloseGeneratedHostLease is not null,
            "ManagedAppHostApiV1 returned an invalid live endpoint table.");
        return api;
    }

    private static unsafe ManagedAppHostApiV1 QueryForwardedManagedAppHostApi(bool isNew)
    {
        var getApi = isNew
            ? (delegate* unmanaged[Cdecl]<uint, uint, uint*, ManagedAppHostApiV1*, int>)
                &NewHost.NativeApi.GetManagedAppHostApiV1
            : (delegate* unmanaged[Cdecl]<uint, uint, uint*, ManagedAppHostApiV1*, int>)
                &OldHost.NativeApi.GetManagedAppHostApiV1;
        uint requiredSize = 0;
        var queryStatus = getApi(
            ManagedAppHostApiV1Exports.Version, 0, &requiredSize, null);
        Require(queryStatus == (int)ManagedAppHostStatusV1.Success &&
                requiredSize == sizeof(ManagedAppHostApiV1),
            "The same-component GetApiV1 forwarder returned an invalid size query.");

        var api = default(ManagedAppHostApiV1);
        var status = getApi(
            ManagedAppHostApiV1Exports.Version,
            (uint)sizeof(ManagedAppHostApiV1),
            &requiredSize,
            &api);
        Require(status == (int)ManagedAppHostStatusV1.Success &&
                api.StructSize == sizeof(ManagedAppHostApiV1) &&
                api.ApiVersion == ManagedAppHostApiV1Exports.Version &&
                api.RegisterGeneratedHostFromPath is not null &&
                api.CloseGeneratedHostLease is not null,
            "The same-component GetApiV1 forwarder returned an invalid table.");
        return api;
    }

    private static unsafe bool AreSameApiTables(
        ManagedAppHostApiV1 left,
        ManagedAppHostApiV1 right) =>
        left.StructSize == right.StructSize &&
        left.ApiVersion == right.ApiVersion &&
        (nint)left.RegisterGeneratedHostFromPath ==
            (nint)right.RegisterGeneratedHostFromPath &&
        (nint)left.CloseGeneratedHostLease == (nint)right.CloseGeneratedHostLease;

#if NETLIFT_MANAGED_HOST_DISPATCH_SMOKE
    private static unsafe int ConfigureNativeCallbacks(bool isNew)
    {
        var configure = isNew
            ? (delegate* unmanaged[Cdecl]<nint, nint, int>)
                &NewAsyncRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.ConfigureNativeCallbacks
            : (delegate* unmanaged[Cdecl]<nint, nint, int>)
                &OldAsyncRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.ConfigureNativeCallbacks;
        return configure(
            (nint)(delegate* unmanaged[Cdecl]<
                ulong, ulong, ulong, ulong, uint, uint, byte*, uint, int>)&OnNativeRequest,
            (nint)(delegate* unmanaged[Cdecl]<
                ulong, ulong, ulong, uint, byte*, uint, byte*, uint, void>)&OnNativePublishResult);
    }

    private static unsafe int CallStartRequest(
        bool isNew,
        ulong endpointToken,
        ulong registrationToken,
        ulong registrationGeneration,
        ulong operationToken,
        byte[] request)
    {
        fixed (byte* requestPointer = request)
        {
            var start = isNew
                ? (delegate* unmanaged[Cdecl]<ulong, ulong, ulong, ulong, byte*, uint, int>)
                    &NewAsyncRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.StartRequestV2
                : (delegate* unmanaged[Cdecl]<ulong, ulong, ulong, ulong, byte*, uint, int>)
                    &OldAsyncRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.StartRequestV2;
            return start(
                endpointToken,
                registrationToken,
                registrationGeneration,
                operationToken,
                requestPointer,
                (uint)request.Length);
        }
    }

    private static unsafe int CallReplyConfirmation(
        bool isNew,
        ulong registrationToken,
        ulong registrationGeneration,
        ulong requestToken)
    {
        var reply = isNew
            ? (delegate* unmanaged[Cdecl]<ulong, ulong, ulong, uint, uint, byte*, uint, int>)
                &NewAsyncRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.ReplyConfirmationV2
            : (delegate* unmanaged[Cdecl]<ulong, ulong, ulong, uint, uint, byte*, uint, int>)
                &OldAsyncRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.ReplyConfirmationV2;
        return reply(
            registrationToken,
            registrationGeneration,
            requestToken,
            (uint)LiftNativeReplyKind.Result,
            1,
            null,
            0);
    }

    private static unsafe int CallDecodeResult(
        bool isNew,
        ulong endpointToken,
        byte[] descriptor,
        byte[] result,
        out int code,
        out uint accepted,
        out uint audited)
    {
        code = 0;
        accepted = 0;
        audited = 0;
        var decodedCode = 0;
        uint decodedAccepted = 0;
        uint decodedAudited = 0;
        fixed (byte* descriptorPointer = descriptor)
        fixed (byte* resultPointer = result)
        {
            var decode = isNew
                ? (delegate* unmanaged[Cdecl]<ulong, byte*, uint, byte*, uint, int*, uint*, uint*, int>)
                    &NewAsyncRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.DecodeResult
                : (delegate* unmanaged[Cdecl]<ulong, byte*, uint, byte*, uint, int*, uint*, uint*, int>)
                    &OldAsyncRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.DecodeResult;
            var status = decode(
                endpointToken,
                descriptorPointer,
                (uint)descriptor.Length,
                resultPointer,
                (uint)result.Length,
                &decodedCode,
                &decodedAccepted,
                &decodedAudited);
            code = decodedCode;
            accepted = decodedAccepted;
            audited = decodedAudited;
            return status;
        }
    }

    private static unsafe int CallOutstandingManagedWork(bool isNew)
    {
        var getOutstanding = isNew
            ? (delegate* unmanaged[Cdecl]<int>)
                &NewAsyncRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.GetOutstandingManagedWork
            : (delegate* unmanaged[Cdecl]<int>)
                &OldAsyncRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.GetOutstandingManagedWork;
        return getOutstanding();
    }

    private static unsafe int CallInvalidateManagedResultContext(
        bool isNew,
        ulong endpointToken)
    {
        var invalidate = isNew
            ? (delegate* unmanaged[Cdecl]<ulong, int>)
                &NewAsyncRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.InvalidateManagedResultContext
            : (delegate* unmanaged[Cdecl]<ulong, int>)
                &OldAsyncRuntime::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi.InvalidateManagedResultContext;
        return invalidate(endpointToken);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int OnNativeRequest(
        ulong registrationToken,
        ulong registrationGeneration,
        ulong requestToken,
        ulong operationToken,
        uint serviceId,
        uint methodId,
        byte* payload,
        uint payloadLength)
    {
        try
        {
            if (registrationToken != _expectedRegistrationToken ||
                registrationGeneration != _expectedRegistrationGeneration ||
                operationToken != _expectedOperationToken ||
                serviceId != _expectedNativeServiceId ||
                methodId != _expectedNativeMethodId ||
                requestToken == 0 ||
                payload is null ||
                payloadLength == 0)
            {
                _nativeRequestSeen?.TrySetException(
                    new InvalidOperationException("The generated route sent an unexpected typed callback."));
                return 0;
            }

            _nativeRequestSeen?.TrySetResult(requestToken);
            return 1;
        }
        catch (Exception exception)
        {
            _nativeRequestSeen?.TrySetException(exception);
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnNativePublishResult(
        ulong registrationToken,
        ulong registrationGeneration,
        ulong operationToken,
        uint status,
        byte* result,
        uint resultLength,
        byte* error,
        uint errorLength)
    {
        try
        {
            if (registrationToken != _expectedRegistrationToken ||
                registrationGeneration != _expectedRegistrationGeneration ||
                operationToken != _expectedOperationToken)
                throw new InvalidOperationException("The generated route published an unexpected operation.");

            var resultBytes = resultLength == 0
                ? []
                : new ReadOnlySpan<byte>(result, checked((int)resultLength)).ToArray();
            var errorBytes = errorLength == 0
                ? []
                : new ReadOnlySpan<byte>(error, checked((int)errorLength)).ToArray();
            _publishedResult?.TrySetResult(new PublishedResult(status, resultBytes, errorBytes));
        }
        catch (Exception exception)
        {
            _publishedResult?.TrySetException(exception);
        }
    }
#endif

    private static unsafe int CallRegister(
        ManagedAppHostApiV1 api,
        ulong endpointToken,
        byte[] nlps,
        byte[] nlmr,
        byte[] localManifest,
        string modulePath,
        string providerTypeName,
        out ulong leaseId)
    {
        var pathBytes = new UTF8Encoding(false, true).GetBytes(modulePath);
        var providerBytes = new UTF8Encoding(false, true).GetBytes(providerTypeName);
        ulong returnedLeaseId = ulong.MaxValue;
        fixed (byte* nlpsPointer = nlps)
        fixed (byte* nlmrPointer = nlmr)
        fixed (byte* manifestPointer = localManifest)
        fixed (byte* pathPointer = pathBytes)
        fixed (byte* providerPointer = providerBytes)
        {
            var status = api.RegisterGeneratedHostFromPath(
                endpointToken,
                nlpsPointer,
                (uint)nlps.Length,
                nlmrPointer,
                (uint)nlmr.Length,
                manifestPointer,
                (uint)localManifest.Length,
                pathPointer,
                (uint)pathBytes.Length,
                providerPointer,
                (uint)providerBytes.Length,
                &returnedLeaseId);
            leaseId = returnedLeaseId;
            return status;
        }
    }

    private static unsafe int CallClose(ManagedAppHostApiV1 api, ulong leaseId) =>
        api.CloseGeneratedHostLease(leaseId);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

#if NETLIFT_MANAGED_HOST_DISPATCH_SMOKE
    private sealed record PublishedResult(uint Status, byte[] Result, byte[] Error);
#endif

    private interface ILiveEndpointApi
    {
        byte[] GetManifest();
        ulong CreateEndpoint();
        void Start(ulong endpointToken, byte[] peerManifest, byte[] peerHello, byte[] trustedPeerHash);
        byte[] GetPeerRequestSchema(ulong endpointToken);
        byte[] GetManagedResultSchema(ulong endpointToken, uint serviceId, uint methodId);
        uint CloseEndpoint(ulong endpointToken);
        void SetTestingHooks(Action? beforePublish, Action? beforeCloseGate, Action? afterCloseGate);
    }

    private sealed unsafe class OldEndpointApi : ILiveEndpointApi
    {
        public byte[] GetManifest()
        {
            var output = new OldHost.LiftV5OwnedBytes
            {
                StructSize = (uint)sizeof(OldHost.LiftV5OwnedBytes)
            };
            var status = NewStatus();
            var getManifest = (delegate* unmanaged[Cdecl]<
                OldHost.LiftV5OwnedBytes*, OldHost.LiftV5Status*, uint>)&OldHost.NativeApi.GetManifest;
            RequireSuccess(getManifest(&output, &status), status, "OLD GetManifest");
            try
            {
                return new ReadOnlySpan<byte>(output.Data, checked((int)output.Length)).ToArray();
            }
            finally
            {
                FreeBuffer(output.Data);
            }
        }

        public ulong CreateEndpoint()
        {
            var status = NewStatus();
            ulong endpointToken = 0;
            var create = (delegate* unmanaged[Cdecl]<
                uint, uint, uint, ulong*, OldHost.LiftV5Status*, uint>)&OldHost.NativeApi.CreateEndpoint;
            RequireSuccess(create(1, 1, 0, &endpointToken, &status), status, "OLD CreateEndpoint");
            Require(endpointToken != 0, "OLD CreateEndpoint returned an empty token.");
            return endpointToken;
        }

        public void Start(
            ulong endpointToken,
            byte[] peerManifest,
            byte[] peerHello,
            byte[] trustedPeerHash)
        {
            var status = NewStatus();
            fixed (byte* manifestPointer = peerManifest)
            fixed (byte* helloPointer = peerHello)
            fixed (byte* hashPointer = trustedPeerHash)
            {
                var manifestView = new OldHost.LiftV5BytesView
                {
                    StructSize = (uint)sizeof(OldHost.LiftV5BytesView),
                    Data = manifestPointer,
                    Length = (uint)peerManifest.Length
                };
                var helloView = new OldHost.LiftV5BytesView
                {
                    StructSize = (uint)sizeof(OldHost.LiftV5BytesView),
                    Data = helloPointer,
                    Length = (uint)peerHello.Length
                };
                var hashView = new OldHost.LiftV5BytesView
                {
                    StructSize = (uint)sizeof(OldHost.LiftV5BytesView),
                    Data = hashPointer,
                    Length = (uint)trustedPeerHash.Length
                };
                var start = (delegate* unmanaged[Cdecl]<
                    ulong,
                    OldHost.LiftV5BytesView*,
                    OldHost.LiftV5BytesView*,
                    OldHost.LiftV5BytesView*,
                    uint,
                    uint,
                    uint,
                    OldHost.LiftV5Status*,
                    uint>)&OldHost.NativeApi.Start;
                RequireSuccess(
                    start(
                        endpointToken,
                        &manifestView,
                        &helloView,
                        &hashView,
                        1,
                        0,
                        1,
                        &status),
                    status,
                    "OLD Start");
            }
        }

        public byte[] GetPeerRequestSchema(ulong endpointToken)
        {
            var output = new OldHost.LiftV5OwnedPeerSchema
            {
                StructSize = (uint)sizeof(OldHost.LiftV5OwnedPeerSchema),
                Version = 1
            };
            var status = NewStatus();
            var get = (delegate* unmanaged[Cdecl]<
                ulong,
                OldHost.LiftV5OwnedPeerSchema*,
                OldHost.LiftV5Status*,
                uint>)&OldHost.NativeApi.GetPeerSchema;
            RequireSuccess(get(endpointToken, &output, &status), status, "OLD GetPeerSchema");
            try
            {
                return new ReadOnlySpan<byte>(output.Data, checked((int)output.Length)).ToArray();
            }
            finally
            {
                FreeBuffer(output.Data);
            }
        }

        public byte[] GetManagedResultSchema(ulong endpointToken, uint serviceId, uint methodId) =>
            ReadManagedResultSchema(
                (delegate* unmanaged[Cdecl]<
                    ulong, uint, uint, byte*, uint, uint*, uint*, uint>)&OldHost.NativeApi.GetManagedResultSchema,
                endpointToken,
                serviceId,
                methodId);

        public uint CloseEndpoint(ulong endpointToken)
        {
            var status = NewStatus();
            var close = (delegate* unmanaged[Cdecl]<
                ulong, OldHost.LiftV5Status*, uint>)&OldHost.NativeApi.CloseEndpoint;
            return close(endpointToken, &status);
        }

        public void SetTestingHooks(Action? beforePublish, Action? beforeCloseGate, Action? afterCloseGate)
        {
            OldHost.NativeApi.BeforeManagedAppBindingPublishForTesting = beforePublish;
            OldHost.NativeApi.BeforeCloseEndpointGateForTesting = beforeCloseGate;
            OldHost.NativeApi.AfterCloseEndpointGateForTesting = afterCloseGate;
        }

        private static OldHost.LiftV5Status NewStatus() =>
            new() { StructSize = (uint)sizeof(OldHost.LiftV5Status) };

        private static void FreeBuffer(void* buffer)
        {
            var status = NewStatus();
            var free = (delegate* unmanaged[Cdecl]<
                void*, OldHost.LiftV5Status*, uint>)&OldHost.NativeApi.FreeBuffer;
            RequireSuccess(free(buffer, &status), status, "OLD FreeBuffer");
        }

        private static void RequireSuccess(uint result, OldHost.LiftV5Status status, string operation)
        {
            if (result != 0 || status.Code != 0)
                throw new InvalidOperationException(
                    $"{operation} failed with status {result}/{status.Code}.");
        }
    }

    private sealed unsafe class NewEndpointApi : ILiveEndpointApi
    {
        public byte[] GetManifest()
        {
            var output = new NewHost.LiftV5OwnedBytes
            {
                StructSize = (uint)sizeof(NewHost.LiftV5OwnedBytes)
            };
            var status = NewStatus();
            var getManifest = (delegate* unmanaged[Cdecl]<
                NewHost.LiftV5OwnedBytes*, NewHost.LiftV5Status*, uint>)&NewHost.NativeApi.GetManifest;
            RequireSuccess(getManifest(&output, &status), status, "NEW GetManifest");
            try
            {
                return new ReadOnlySpan<byte>(output.Data, checked((int)output.Length)).ToArray();
            }
            finally
            {
                FreeBuffer(output.Data);
            }
        }

        public ulong CreateEndpoint()
        {
            var status = NewStatus();
            ulong endpointToken = 0;
            var create = (delegate* unmanaged[Cdecl]<
                uint, uint, uint, ulong*, NewHost.LiftV5Status*, uint>)&NewHost.NativeApi.CreateEndpoint;
            RequireSuccess(create(1, 1, 0, &endpointToken, &status), status, "NEW CreateEndpoint");
            Require(endpointToken != 0, "NEW CreateEndpoint returned an empty token.");
            return endpointToken;
        }

        public void Start(
            ulong endpointToken,
            byte[] peerManifest,
            byte[] peerHello,
            byte[] trustedPeerHash)
        {
            var status = NewStatus();
            fixed (byte* manifestPointer = peerManifest)
            fixed (byte* helloPointer = peerHello)
            fixed (byte* hashPointer = trustedPeerHash)
            {
                var manifestView = new NewHost.LiftV5BytesView
                {
                    StructSize = (uint)sizeof(NewHost.LiftV5BytesView),
                    Data = manifestPointer,
                    Length = (uint)peerManifest.Length
                };
                var helloView = new NewHost.LiftV5BytesView
                {
                    StructSize = (uint)sizeof(NewHost.LiftV5BytesView),
                    Data = helloPointer,
                    Length = (uint)peerHello.Length
                };
                var hashView = new NewHost.LiftV5BytesView
                {
                    StructSize = (uint)sizeof(NewHost.LiftV5BytesView),
                    Data = hashPointer,
                    Length = (uint)trustedPeerHash.Length
                };
                var start = (delegate* unmanaged[Cdecl]<
                    ulong,
                    NewHost.LiftV5BytesView*,
                    NewHost.LiftV5BytesView*,
                    NewHost.LiftV5BytesView*,
                    uint,
                    uint,
                    uint,
                    NewHost.LiftV5Status*,
                    uint>)&NewHost.NativeApi.Start;
                RequireSuccess(
                    start(
                        endpointToken,
                        &manifestView,
                        &helloView,
                        &hashView,
                        1,
                        0,
                        1,
                        &status),
                    status,
                    "NEW Start");
            }
        }

        public byte[] GetPeerRequestSchema(ulong endpointToken)
        {
            var output = new NewHost.LiftV5OwnedPeerSchema
            {
                StructSize = (uint)sizeof(NewHost.LiftV5OwnedPeerSchema),
                Version = 1
            };
            var status = NewStatus();
            var get = (delegate* unmanaged[Cdecl]<
                ulong,
                NewHost.LiftV5OwnedPeerSchema*,
                NewHost.LiftV5Status*,
                uint>)&NewHost.NativeApi.GetPeerSchema;
            RequireSuccess(get(endpointToken, &output, &status), status, "NEW GetPeerSchema");
            try
            {
                return new ReadOnlySpan<byte>(output.Data, checked((int)output.Length)).ToArray();
            }
            finally
            {
                FreeBuffer(output.Data);
            }
        }

        public byte[] GetManagedResultSchema(ulong endpointToken, uint serviceId, uint methodId) =>
            ReadManagedResultSchema(
                (delegate* unmanaged[Cdecl]<
                    ulong, uint, uint, byte*, uint, uint*, uint*, uint>)&NewHost.NativeApi.GetManagedResultSchema,
                endpointToken,
                serviceId,
                methodId);

        public uint CloseEndpoint(ulong endpointToken)
        {
            var status = NewStatus();
            var close = (delegate* unmanaged[Cdecl]<
                ulong, NewHost.LiftV5Status*, uint>)&NewHost.NativeApi.CloseEndpoint;
            return close(endpointToken, &status);
        }

        public void SetTestingHooks(Action? beforePublish, Action? beforeCloseGate, Action? afterCloseGate)
        {
            NewHost.NativeApi.BeforeManagedAppBindingPublishForTesting = beforePublish;
            NewHost.NativeApi.BeforeCloseEndpointGateForTesting = beforeCloseGate;
            NewHost.NativeApi.AfterCloseEndpointGateForTesting = afterCloseGate;
        }

        private static NewHost.LiftV5Status NewStatus() =>
            new() { StructSize = (uint)sizeof(NewHost.LiftV5Status) };

        private static void FreeBuffer(void* buffer)
        {
            var status = NewStatus();
            var free = (delegate* unmanaged[Cdecl]<
                void*, NewHost.LiftV5Status*, uint>)&NewHost.NativeApi.FreeBuffer;
            RequireSuccess(free(buffer, &status), status, "NEW FreeBuffer");
        }

        private static void RequireSuccess(uint result, NewHost.LiftV5Status status, string operation)
        {
            if (result != 0 || status.Code != 0)
                throw new InvalidOperationException(
                    $"{operation} failed with status {result}/{status.Code}.");
        }
    }

    private static unsafe byte[] ReadManagedResultSchema(
        delegate* unmanaged[Cdecl]<ulong, uint, uint, byte*, uint, uint*, uint*, uint> getSchema,
        ulong endpointToken,
        uint serviceId,
        uint methodId)
    {
        uint bytesWritten = 0;
        uint requiredBytes = 0;
        var queryStatus = getSchema(
            endpointToken,
            serviceId,
            methodId,
            null,
            0,
            &bytesWritten,
            &requiredBytes);
        Require(queryStatus == SchemaBufferTooSmall &&
                bytesWritten == 0 &&
                requiredBytes is >= 136 and <= ManagedAppHostApiV1Exports.MaximumNlmrBytes,
            $"Managed-result schema query failed with status {queryStatus}.");

        var bytes = new byte[checked((int)requiredBytes)];
        fixed (byte* destination = bytes)
        {
            var status = getSchema(
                endpointToken,
                serviceId,
                methodId,
                destination,
                (uint)bytes.Length,
                &bytesWritten,
                &requiredBytes);
            Require(status == Success &&
                    bytesWritten == bytes.Length &&
                    requiredBytes == bytes.Length,
                $"Managed-result schema copy failed with status {status}.");
        }
        return bytes;
    }
}
