using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using NetLift;
using NetLift.ManagedHost;
using NetLift.Negotiation;

internal static class Program
{
    private const int Success = (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Success;
    private const int Busy = (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.Busy;
    private const int SchemaMismatch = (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.SchemaMismatch;
    private const int ManagedFailure = (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.ManagedFailure;
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "captured")
        {
            try
            {
                CapturedManagedLoadSmoke.Run(args[1..]);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        if (args.Length > 0 && args[0] == "captured-registration")
        {
            try
            {
                await CapturedManagedRegistrationSmoke.RunAsync(args[1..]).ConfigureAwait(false);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        if (args.Length is < 4 or > 5 ||
            args[0] is not ("old" or "new") ||
            args[1] is not (
                "route" or "rollback" or "missing-dependency" or "bad-dependency" or
                "incompatible-reference" or "incompatible-copy" or
                "schema-mismatch" or "close-failure" or
                "api-abi" or "api-route" or "api-binding-mismatch" or
                "api-cross-hash-mismatch" or
                "api-live-route" or
                "api-source-authority" or
                "api-source-authority-cdecl" or
                "api-manifest-mismatch" or "api-missing-module" or
                "api-provider-mismatch" or "api-stale-endpoint" or
                "api-close-dispose-failure") ||
            (args[1] == "incompatible-copy") != (args.Length == 5))
        {
            Console.Error.WriteLine(
                "Usage: NetLift.ManagedHost.Smoke <old|new> <mode> <module-assembly> <scratch-root> [incompatible-contracts-assembly]");
            return 2;
        }

        try
        {
            await RunAsync(
                args[0] == "new",
                args[1],
                args[2],
                args[3],
                args.Length == 5 ? args[4] : null);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task RunAsync(
        bool isNew,
        string mode,
        string moduleAssemblyPath,
        string scratchRoot,
        string? incompatibleContractsPath)
    {
        const ulong endpointToken = 8301;
        var providerTypeName =
            "NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider";
        var moduleName = AssemblyName.GetAssemblyName(moduleAssemblyPath).Name
            ?? throw new InvalidOperationException("The app module assembly has no name.");

        if (mode == "incompatible-reference")
        {
            var exception = CaptureException(() =>
                ManagedAppModuleLease.Load(moduleAssemblyPath, providerTypeName));
            Require(exception is FileLoadException &&
                    exception.Message.Contains(
                        "incompatible shared assembly", StringComparison.OrdinalIgnoreCase),
                "The loader accepted a module with an incompatible Contracts AssemblyRef.");
            Console.WriteLine("PASS: actual incompatible Contracts AssemblyRef rejected before module load.");
            return;
        }

        Require(!IsLoadedInDefaultContext(moduleName),
            $"The {moduleName} app module was already loaded in the host default ALC.");
        ValidateContractReferencePreflight(moduleAssemblyPath, providerTypeName, scratchRoot, moduleName);

        if (mode == "incompatible-copy")
        {
            ValidateIncompatiblePrivateCopy(
                moduleAssemblyPath,
                providerTypeName,
                scratchRoot,
                incompatibleContractsPath
                    ?? throw new InvalidOperationException("The incompatible Contracts assembly path is required."));
            return;
        }

        if (mode is "missing-dependency" or "bad-dependency")
        {
            Require(!isNew,
                "The OLD module is required for the private-dependency negative case.");
            ValidatePrivateDependencyFailure(
                moduleAssemblyPath, providerTypeName, scratchRoot, mode);
            return;
        }

        if (mode.StartsWith("api-", StringComparison.Ordinal))
        {
            await RunManagedApiAsync(isNew, mode, moduleAssemblyPath);
            return;
        }

        var moduleLease = ManagedAppModuleLease.Load(moduleAssemblyPath, providerTypeName);
        Require(!moduleLease.LoadContext.IsCollectible,
            "The product app module context must be noncollectible.");
        Require(AssemblyLoadContext.GetLoadContext(moduleLease.ModuleAssembly) == moduleLease.LoadContext,
            "The app module assembly was not loaded into the product-owned ALC.");
        Require(!IsLoadedInDefaultContext(moduleName),
            "The app module assembly was loaded into the host default ALC.");

        var hostContracts = typeof(ILiftAsyncCallbackV1ManagedHostModuleProviderV2).Assembly;
        var contextContracts = moduleLease.LoadContext.LoadFromAssemblyName(hostContracts.GetName());
        Require(ReferenceEquals(hostContracts, contextContracts),
            "The app module ALC did not resolve the exact host Contracts assembly.");
        Require(ReferenceEquals(
                moduleLease.Provider.GetType().GetInterfaces()
                    .Single(type => type.Name == nameof(
                        ILiftAsyncCallbackV1ManagedHostModuleProviderV2)).Assembly,
                hostContracts),
            "The app provider did not implement the host's exact typed provider contract.");
        Require(!moduleLease.LoadContext.Assemblies.Any(
                assembly => string.Equals(
                    assembly.GetName().Name,
                    hostContracts.GetName().Name,
                    StringComparison.OrdinalIgnoreCase)),
            "A private Contracts assembly was loaded into the app module ALC.");
        var hostNegotiation = typeof(LiftAsyncCallbackV1ManagedBridge).Assembly;
        Require(ReferenceEquals(
                hostNegotiation,
                moduleLease.LoadContext.LoadFromAssemblyName(hostNegotiation.GetName())),
            "The app module ALC did not bind the shared Negotiation assembly to the host identity.");
        Require(CaptureException(() =>
                    ManagedAppModuleLease.Load(moduleAssemblyPath, providerTypeName))
                is InvalidOperationException,
            "The process loader accepted a second app-module identity/load attempt.");

        var invocation = CreateInvocation(isNew, !isNew, endpointToken, 1, true);
        var localHash = moduleLease.SchemaHash;
        Require(CryptographicOperations.FixedTimeEquals(
                localHash, invocation.Descriptor.LocalSchemaHash.Span),
            "The loaded app module table pin did not match the synthetic local descriptor.");
        Require(!CryptographicOperations.FixedTimeEquals(
                invocation.Descriptor.LocalSchemaHash.Span,
                invocation.Descriptor.PeerSchemaHash.Span),
            "The synthetic cross-version descriptor unexpectedly used matching pins.");

        Console.WriteLine(
            "Authority: synthetic NLPS/NLMR descriptor bytes; no live native endpoint was used.");

        if (mode == "rollback")
        {
            await RunRollbackQuarantineAsync(
                moduleLease, isNew, invocation.Descriptor, endpointToken);
            return;
        }

        if (mode == "schema-mismatch")
        {
            RunSchemaMismatch(moduleLease, invocation.Descriptor);
            return;
        }

        if (mode == "close-failure")
        {
            RunCloseFailure(moduleLease, isNew, invocation.Descriptor, endpointToken);
            return;
        }

        await RunGeneratedRouteAsync(moduleLease, isNew, endpointToken, invocation.Descriptor);
    }

    private static async Task RunManagedApiAsync(
        bool isNew,
        string mode,
        string moduleAssemblyPath)
    {
        if (mode == "api-live-route")
        {
            await LiveEndpointBindingSmoke.RunAsync(isNew, moduleAssemblyPath);
            return;
        }
        if (mode == "api-source-authority")
        {
            await LiveEndpointBindingSmoke.RunSourceAuthorityAsync(isNew, moduleAssemblyPath);
            return;
        }
        if (mode == "api-source-authority-cdecl")
        {
            await LiveEndpointBindingSmoke.RunSourceAuthorityCdeclAsync(isNew, moduleAssemblyPath);
            return;
        }

        const ulong endpointToken = 9817;
        var descriptor = CreateInvocation(
            isNew, !isNew, endpointToken, 1, true).Descriptor;
        if (mode == "api-cross-hash-mismatch")
        {
            var mismatchedNlmr = descriptor.ManagedResultSchema.ToArray();
            mismatchedNlmr[104] ^= 0x01;
            descriptor = descriptor with { ManagedResultSchema = mismatchedNlmr };
        }
        var registry = new AsyncCallbackV1SmokeSupport.AsyncCallbackV1SmokeRegistry(descriptor);
        var resultContext = new TestResultContextLease();
        var provider = new TestEndpointBindingProvider(descriptor, registry, resultContext);
        var localManifest = ReadCanonicalManifest(moduleAssemblyPath);
        var manifestText = new UTF8Encoding(false, true).GetString(localManifest);
        const string manifestHashMarker = "\"schemaHash\":\"";
        var manifestHashStart = manifestText.IndexOf(
            manifestHashMarker, StringComparison.Ordinal);
        Require(manifestHashStart >= 0 &&
                CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(manifestText.Substring(
                        manifestHashStart + manifestHashMarker.Length, 64)),
                    descriptor.LocalSchemaHash.Span),
            "The test's native local manifest does not carry the selected OLD/NEW schema pin.");
        var api = QueryManagedAppHostApi();

        if (mode == "api-abi")
        {
            var status = CallRegister(
                api, endpointToken, descriptor.PeerRequestSchema.ToArray(),
                descriptor.ManagedResultSchema.ToArray(), localManifest,
                moduleAssemblyPath, ProviderTypeName, out var leaseId);
            Require(status == (int)ManagedAppHostStatusV1.StaleHandle &&
                    leaseId == 0 &&
                    ManagedAppHostApiV1Exports.OutstandingLeaseCount == 0,
                "The source-owned endpoint provider accepted an unissued token or retained a lease.");
            TestInvalidUtf8Path(api, descriptor, localManifest);
            TestInvalidInputBuffers(api, descriptor, localManifest, moduleAssemblyPath);
            Console.WriteLine(
                "PASS: private ManagedAppHostApiV1 size/version/short-no-write and unconfigured-host checks.");
            return;
        }

        ManagedAppHostApiV1Exports.ConfigureTestEndpointBindingProvider(provider);
        switch (mode)
        {
            case "api-route":
                await RunManagedApiRouteAsync(
                    api, isNew, endpointToken, moduleAssemblyPath,
                    localManifest, descriptor, registry, resultContext);
                break;
            case "api-binding-mismatch":
            {
                var mismatchedNlps = descriptor.PeerRequestSchema.ToArray();
                mismatchedNlps[^1] ^= 0x01;
                var status = CallRegister(
                    api, endpointToken, mismatchedNlps,
                    descriptor.ManagedResultSchema.ToArray(), localManifest,
                    moduleAssemblyPath, ProviderTypeName, out var leaseId);
                Require(status == (int)ManagedAppHostStatusV1.SchemaMismatch &&
                        leaseId == 0 &&
                        provider.AcquisitionCount == 1 &&
                        resultContext.DisposeCount == 1 &&
                        GeneratedHostRegistration.OutstandingRegistrationCount == 0 &&
                        ManagedAppHostApiV1Exports.OutstandingLeaseCount == 0 &&
                        ManagedAppModuleLease.FailedLoadRootCount == 0,
                    "A C snapshot that differed from the independently acquired NLPS binding was published.");
                Console.WriteLine(
                    "PASS: independently acquired NLPS/NLMR byte mismatch rejected before module load or route publication.");
                break;
            }
            case "api-cross-hash-mismatch":
            {
                var status = CallRegister(
                    api, endpointToken, descriptor.PeerRequestSchema.ToArray(),
                    descriptor.ManagedResultSchema.ToArray(), localManifest,
                    moduleAssemblyPath, ProviderTypeName, out var leaseId);
                Require(status == (int)ManagedAppHostStatusV1.SchemaMismatch &&
                        leaseId == 0 &&
                        resultContext.DisposeCount == 1 &&
                        GeneratedHostRegistration.OutstandingRegistrationCount == 0 &&
                        ManagedAppModuleLease.FailedLoadRootCount == 0,
                    "The provider's internally inconsistent NLPS/NLMR peer hashes were accepted.");
                Console.WriteLine(
                    "PASS: internally inconsistent NLPS/NLMR peer hashes rejected before module load.");
                break;
            }
            case "api-manifest-mismatch":
            {
                var mismatchedManifest = localManifest.ToArray();
                mismatchedManifest[0] ^= 0x01;
                var status = CallRegister(
                    api, endpointToken, descriptor.PeerRequestSchema.ToArray(),
                    descriptor.ManagedResultSchema.ToArray(), mismatchedManifest,
                    moduleAssemblyPath, ProviderTypeName, out var leaseId);
                Require(status == (int)ManagedAppHostStatusV1.SchemaMismatch &&
                        leaseId != 0 &&
                        resultContext.DisposeCount == 0 &&
                        CallClose(api, leaseId) == (int)ManagedAppHostStatusV1.Success &&
                        resultContext.DisposeCount == 1 &&
                        ManagedAppModuleLease.FailedLoadRootCount == 0,
                    "The module/local-manifest mismatch did not return a cleanup lease or close cleanly.");
                Console.WriteLine(
                    "PASS: native local-manifest mismatch rejected after module preflight without route publication and with explicit cleanup lease.");
                break;
            }
            case "api-missing-module":
            {
                var missingPath = Path.Combine(
                    Path.GetDirectoryName(moduleAssemblyPath)!,
                    "missing-managed-app-module.dll");
                var status = CallRegister(
                    api, endpointToken, descriptor.PeerRequestSchema.ToArray(),
                    descriptor.ManagedResultSchema.ToArray(), localManifest,
                    missingPath, ProviderTypeName, out var leaseId);
                Require(status == (int)ManagedAppHostStatusV1.ModuleUnavailable &&
                        leaseId == 0 &&
                        resultContext.DisposeCount == 1 &&
                        ManagedAppHostApiV1Exports.OutstandingLeaseCount == 0 &&
                        ManagedAppModuleLease.FailedLoadRootCount == 0,
                    "A missing module path did not fail atomically without retained roots.");
                Console.WriteLine(
                    "PASS: missing module path returned ModuleUnavailable with zero lease and disposed binding.");
                break;
            }
            case "api-provider-mismatch":
            {
                var status = CallRegister(
                    api, endpointToken, descriptor.PeerRequestSchema.ToArray(),
                    descriptor.ManagedResultSchema.ToArray(), localManifest,
                    moduleAssemblyPath, "Missing.Provider.Type", out var leaseId);
                Require(status == (int)ManagedAppHostStatusV1.ManagedFailure &&
                        leaseId != 0 &&
                        provider.AcquisitionCount == 1 &&
                        resultContext.DisposeCount == 0 &&
                        ManagedAppHostApiV1Exports.OutstandingLeaseCount == 1 &&
                        ManagedAppModuleLease.FailedLoadRootCount == 1 &&
                        CallClose(api, leaseId) == (int)ManagedAppHostStatusV1.ManagedFailure &&
                        resultContext.DisposeCount == 0 &&
                        ManagedAppHostApiV1Exports.OutstandingLeaseCount == 1,
                    "A provider activation failure did not return a permanently quarantined non-dispatchable lease.");
                Console.WriteLine(
                    "PASS: failed post-ALC provider activation returned a permanent opaque quarantine lease.");
                break;
            }
            case "api-stale-endpoint":
            {
                var status = CallRegister(
                    api, endpointToken + 1, descriptor.PeerRequestSchema.ToArray(),
                    descriptor.ManagedResultSchema.ToArray(), localManifest,
                    moduleAssemblyPath, ProviderTypeName, out var leaseId);
                Require(status == (int)ManagedAppHostStatusV1.StaleHandle &&
                        leaseId == 0 &&
                        provider.AcquisitionCount == 1 &&
                        resultContext.DisposeCount == 0 &&
                        ManagedAppHostApiV1Exports.OutstandingLeaseCount == 0,
                    "An unknown endpoint token did not fail closed without acquiring or publishing a binding.");
                Console.WriteLine("PASS: unknown endpoint token rejected without a managed lease.");
                break;
            }
            case "api-close-dispose-failure":
            {
                var leaseId = RegisterManagedApiRoute(
                    api, endpointToken, moduleAssemblyPath, localManifest, descriptor);
                resultContext.FailuresRemaining = 1;
                var firstClose = CallClose(api, leaseId);
                Require(firstClose == (int)ManagedAppHostStatusV1.ManagedFailure &&
                        resultContext.DisposeCount == 1 &&
                        !resultContext.IsDisposed &&
                        GeneratedHostRegistration.OutstandingRegistrationCount == 0 &&
                        ManagedAppHostApiV1Exports.OutstandingLeaseCount == 1,
                    "A result-context disposal failure released the opaque endpoint lease.");
                var retryClose = CallClose(api, leaseId);
                Require(retryClose == (int)ManagedAppHostStatusV1.Success &&
                        resultContext.DisposeCount == 2 &&
                        resultContext.IsDisposed &&
                        ManagedAppHostApiV1Exports.OutstandingLeaseCount == 0 &&
                        CallClose(api, leaseId) == (int)ManagedAppHostStatusV1.AlreadyClosed &&
                        CallClose(api, ulong.MaxValue) == (int)ManagedAppHostStatusV1.StaleHandle,
                    "Successful retry did not release the lease once or stale close was misreported.");
                Console.WriteLine(
                    "PASS: failed result-context disposal retained opaque lease; retry closed it; repeated close returned AlreadyClosed.");
                break;
            }
            default:
                throw new InvalidOperationException($"Unknown managed host API mode '{mode}'.");
        }
    }

    private static async Task RunManagedApiRouteAsync(
        ManagedAppHostApiV1 api,
        bool isNew,
        ulong endpointToken,
        string moduleAssemblyPath,
        byte[] localManifest,
        LiftAsyncCallbackV1HostDescriptor descriptor,
        AsyncCallbackV1SmokeSupport.AsyncCallbackV1SmokeRegistry registry,
        TestResultContextLease resultContext)
    {
        var registrationCountBefore = GeneratedHostRegistration.OutstandingRegistrationCount;
        var leaseId = RegisterManagedApiRoute(
            api, endpointToken, moduleAssemblyPath, localManifest, descriptor);
        Require(leaseId != 0 &&
                GeneratedHostRegistration.OutstandingRegistrationCount == registrationCountBefore + 1 &&
                ManagedAppHostApiV1Exports.OutstandingLeaseCount == 1 &&
                !resultContext.IsDisposed &&
                !IsLoadedInDefaultContext(
                    AssemblyName.GetAssemblyName(moduleAssemblyPath).Name!),
            "The path API did not publish one rooted route in the product app-module ALC.");

        var moduleLoadContext = AssemblyLoadContext.All.Single(loadContext =>
            loadContext.Assemblies.Any(assembly =>
                string.Equals(
                    assembly.GetName().Name,
                    AssemblyName.GetAssemblyName(moduleAssemblyPath).Name,
                    StringComparison.OrdinalIgnoreCase)));
        var moduleAssembly = moduleLoadContext.Assemblies
            .Single(assembly =>
                string.Equals(
                    assembly.GetName().Name,
                    AssemblyName.GetAssemblyName(moduleAssemblyPath).Name,
                    StringComparison.OrdinalIgnoreCase) &&
                AssemblyLoadContext.GetLoadContext(assembly) != AssemblyLoadContext.Default);
        var hostContracts = typeof(ILiftAsyncCallbackV1ManagedHostModuleProviderV2).Assembly;
        var hostNegotiation = typeof(LiftAsyncCallbackV1ManagedBridge).Assembly;
        Require(ReferenceEquals(
                    hostContracts,
                    moduleLoadContext.LoadFromAssemblyName(hostContracts.GetName())) &&
                ReferenceEquals(
                    hostNegotiation,
                    moduleLoadContext.LoadFromAssemblyName(hostNegotiation.GetName())) &&
                !moduleLoadContext.Assemblies.Any(assembly =>
                    string.Equals(
                        assembly.GetName().Name,
                        hostContracts.GetName().Name,
                        StringComparison.OrdinalIgnoreCase)),
            "The API-loaded module did not preserve exact host Contracts/Negotiation identity.");
        var probe = GetProbe(moduleAssembly, isNew, endpointToken);
        await DispatchAndAssertAsync(
            registry,
            CreateInvocation(isNew, !isNew, endpointToken, 1, true),
            isNew,
            peerIsNew: !isNew,
            operationId: 1,
            confirmationAccepted: true);
        await DispatchAndAssertAsync(
            registry,
            CreateInvocation(isNew, !isNew, endpointToken, 2, false),
            isNew,
            peerIsNew: !isNew,
            operationId: 2,
            confirmationAccepted: false);

        var pendingInvocation = CreateInvocation(isNew, !isNew, endpointToken, 3, true);
        pendingInvocation.NativeInvoker.DeferredReply =
            new TaskCompletionSource<LiftNativeReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingTask = InvokeRouteAsync(
            registry, pendingInvocation, isNew, peerIsNew: !isNew, operationId: 3);
        await pendingInvocation.NativeInvoker.CallEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Require(CallClose(api, leaseId) == (int)ManagedAppHostStatusV1.Busy &&
                ManagedAppHostApiV1Exports.OutstandingLeaseCount == 1 &&
                !resultContext.IsDisposed &&
                ReadInt(probe, "AcquisitionCount") == 3 &&
                ReadInt(probe, "DisposalCount") == 2,
            "Busy close did not retain the opaque module/result-context lease through drain.");

        var lateInvocation = CreateInvocation(isNew, !isNew, endpointToken, 4, true);
        await ExpectFailureAsync(
            registry.InvokeAsync(
                EncodeRequest(peerIsNew: !isNew, operationId: 4),
                WithOperation(lateInvocation.Context, endpointToken, 4),
                CancellationToken.None).AsTask(),
            "A new route entered after the opaque lease close began.");
        pendingInvocation.NativeInvoker.DeferredReply!.SetResult(
            new LiftNativeReply(
                LiftNativeReplyKind.Result, new byte[] { 1 }, ReadOnlyMemory<byte>.Empty));
        AssertResult(
            await pendingTask, isNew, peerIsNew: !isNew, operationId: 3, accepted: true);

        Require(ReadInt(probe, "AcquisitionCount") == 3 &&
                ReadInt(probe, "DisposalCount") == 3 &&
                CallClose(api, leaseId) == (int)ManagedAppHostStatusV1.Success &&
                resultContext.IsDisposed &&
                resultContext.DisposeCount == 1 &&
                ReadBool(probe, "IsDisposed") &&
                ReadInt(probe, "EndpointDisposalCount") == 1 &&
                GeneratedHostRegistration.OutstandingRegistrationCount == registrationCountBefore &&
                ManagedAppHostApiV1Exports.OutstandingLeaseCount == 0 &&
                CallClose(api, leaseId) == (int)ManagedAppHostStatusV1.AlreadyClosed &&
                CallClose(api, ulong.MaxValue) == (int)ManagedAppHostStatusV1.StaleHandle,
            "Successful drain/close did not release each route/result lease exactly once.");

        Console.WriteLine(
            $"PASS: {(isNew ? "NEW" : "OLD")} ManagedAppHostApiV1 route, typed callbacks, Busy/drain, exact lease release, and close-status semantics.");
    }

    private static unsafe ulong RegisterManagedApiRoute(
        ManagedAppHostApiV1 api,
        ulong endpointToken,
        string moduleAssemblyPath,
        byte[] localManifest,
        LiftAsyncCallbackV1HostDescriptor descriptor)
    {
        var borrowedNlps = descriptor.PeerRequestSchema.ToArray();
        var borrowedNlmr = descriptor.ManagedResultSchema.ToArray();
        var status = CallRegister(
            api,
            endpointToken,
            borrowedNlps,
            borrowedNlmr,
            localManifest,
            moduleAssemblyPath,
            ProviderTypeName,
            out var leaseId);
        Array.Fill(borrowedNlps, (byte)0);
        Array.Fill(borrowedNlmr, (byte)0);
        Array.Fill(localManifest, (byte)0);
        Require(status == (int)ManagedAppHostStatusV1.Success && leaseId != 0,
            $"ManagedAppHostApiV1 route registration failed with status {status}.");
        return leaseId;
    }

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
        var returnedLeaseId = ulong.MaxValue;
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

    private static unsafe ManagedAppHostApiV1 QueryManagedAppHostApi()
    {
        var getApi = (delegate* unmanaged[Cdecl]<
            uint, uint, uint*, ManagedAppHostApiV1*, int>)&ManagedAppHostApiV1Exports.GetApiV1;
        uint requiredSize = 0;
        var queryStatus = getApi(ManagedAppHostApiV1Exports.Version, 0, &requiredSize, null);
        Require(queryStatus == (int)ManagedAppHostStatusV1.Success &&
                requiredSize == sizeof(ManagedAppHostApiV1),
            "ManagedAppHostApiV1 size query returned an invalid size or status.");

        var unsupported = default(ManagedAppHostApiV1);
        new Span<byte>(&unsupported, sizeof(ManagedAppHostApiV1)).Fill(0xA5);
        var unsupportedStatus = getApi(
            ManagedAppHostApiV1Exports.Version + 1,
            (uint)sizeof(ManagedAppHostApiV1),
            &requiredSize,
            &unsupported);
        Require(unsupportedStatus == (int)ManagedAppHostStatusV1.UnsupportedVersion &&
                new ReadOnlySpan<byte>(&unsupported, sizeof(ManagedAppHostApiV1))
                    .ToArray().All(value => value == 0xA5),
            "Unsupported API version modified the output table.");

        Span<byte> shortBuffer = stackalloc byte[sizeof(ManagedAppHostApiV1)];
        shortBuffer.Fill(0x5A);
        fixed (byte* shortPointer = shortBuffer)
        {
            var shortStatus = getApi(
                ManagedAppHostApiV1Exports.Version,
                (uint)sizeof(ManagedAppHostApiV1) - 1,
                &requiredSize,
                (ManagedAppHostApiV1*)shortPointer);
            Require(shortStatus == (int)ManagedAppHostStatusV1.BufferTooSmall &&
                    requiredSize == sizeof(ManagedAppHostApiV1) &&
                    shortBuffer.ToArray().All(value => value == 0x5A),
                "Short ManagedAppHostApiV1 output modified sentinel bytes.");
        }

        var api = default(ManagedAppHostApiV1);
        var getStatus = getApi(
            ManagedAppHostApiV1Exports.Version,
            (uint)sizeof(ManagedAppHostApiV1),
            &requiredSize,
            &api);
        Require(getStatus == (int)ManagedAppHostStatusV1.Success &&
                api.StructSize == sizeof(ManagedAppHostApiV1) &&
                api.ApiVersion == ManagedAppHostApiV1Exports.Version &&
                api.RegisterGeneratedHostFromPath is not null &&
                api.CloseGeneratedHostLease is not null,
            "ManagedAppHostApiV1 returned an invalid function table.");
        return api;
    }

    private static unsafe void TestInvalidUtf8Path(
        ManagedAppHostApiV1 api,
        LiftAsyncCallbackV1HostDescriptor descriptor,
        byte[] localManifest)
    {
        var nlps = descriptor.PeerRequestSchema.ToArray();
        var nlmr = descriptor.ManagedResultSchema.ToArray();
        var path = new byte[] { 0xFF };
        var provider = new UTF8Encoding(false, true).GetBytes(ProviderTypeName);
        ulong leaseId = ulong.MaxValue;
        fixed (byte* nlpsPointer = nlps)
        fixed (byte* nlmrPointer = nlmr)
        fixed (byte* manifestPointer = localManifest)
        fixed (byte* pathPointer = path)
        fixed (byte* providerPointer = provider)
        {
            var status = api.RegisterGeneratedHostFromPath(
                descriptor.EndpointToken,
                nlpsPointer,
                (uint)nlps.Length,
                nlmrPointer,
                (uint)nlmr.Length,
                manifestPointer,
                (uint)localManifest.Length,
                pathPointer,
                (uint)path.Length,
                providerPointer,
                (uint)provider.Length,
                &leaseId);
            Require(status == (int)ManagedAppHostStatusV1.InvalidArgument &&
                    leaseId == 0,
                "The host API accepted an invalid UTF-8 module path.");
        }
    }

    private static unsafe void TestInvalidInputBuffers(
        ManagedAppHostApiV1 api,
        LiftAsyncCallbackV1HostDescriptor descriptor,
        byte[] localManifest,
        string modulePath)
    {
        var nlmr = descriptor.ManagedResultSchema.ToArray();
        var path = new UTF8Encoding(false, true).GetBytes(modulePath);
        var provider = new UTF8Encoding(false, true).GetBytes(ProviderTypeName);
        ulong leaseId = ulong.MaxValue;
        fixed (byte* nlmrPointer = nlmr)
        fixed (byte* manifestPointer = localManifest)
        fixed (byte* pathPointer = path)
        fixed (byte* providerPointer = provider)
        {
            var nullNlpsStatus = api.RegisterGeneratedHostFromPath(
                descriptor.EndpointToken,
                null,
                ManagedAppHostApiV1Exports.MaximumNlpsBytes,
                nlmrPointer,
                (uint)nlmr.Length,
                manifestPointer,
                (uint)localManifest.Length,
                pathPointer,
                (uint)path.Length,
                providerPointer,
                (uint)provider.Length,
                &leaseId);
            Require(nullNlpsStatus == (int)ManagedAppHostStatusV1.InvalidArgument &&
                    leaseId == 0,
                "The API accepted a null NLPS pointer with a nonzero length.");
        }

        var oversizedNlps = new byte[ManagedAppHostApiV1Exports.MaximumNlpsBytes + 1];
        var oversizedStatus = CallRegister(
            api, descriptor.EndpointToken, oversizedNlps, nlmr, localManifest,
            modulePath, ProviderTypeName, out leaseId);
        Require(oversizedStatus == (int)ManagedAppHostStatusV1.InvalidArgument &&
                leaseId == 0,
            "The API accepted an NLPS snapshot over its fixed maximum.");

        var invalidManifestStatus = CallRegister(
            api, descriptor.EndpointToken, descriptor.PeerRequestSchema.ToArray(), nlmr,
            [0xFF], modulePath, ProviderTypeName, out leaseId);
        Require(invalidManifestStatus == (int)ManagedAppHostStatusV1.InvalidArgument &&
                leaseId == 0,
            "The API accepted a local canonical manifest that was not strict UTF-8.");
    }

    private static byte[] ReadCanonicalManifest(string moduleAssemblyPath)
    {
        var moduleDirectory = Path.GetDirectoryName(moduleAssemblyPath)
            ?? throw new InvalidOperationException("The app module path has no containing directory.");
        foreach (var assemblyPath in Directory.EnumerateFiles(
                     moduleDirectory, "*.dll", SearchOption.AllDirectories)
                 .Prepend(moduleAssemblyPath)
                 .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var manifest = TryReadCanonicalManifest(assemblyPath);
                if (manifest is not null)
                    return manifest;
            }
            catch (BadImageFormatException)
            {
            }
        }

        throw new InvalidOperationException(
            "The app module dependency set did not expose its generated canonical manifest constant.");
    }

    private static byte[]? TryReadCanonicalManifest(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
            return null;
        var metadata = peReader.GetMetadataReader();
        foreach (var typeHandle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(typeHandle);
            if (metadata.GetString(type.Name) == "Contract" &&
                metadata.GetString(type.Namespace) == "NetLift.Generated")
            {
                foreach (var fieldHandle in type.GetFields())
                {
                    var field = metadata.GetFieldDefinition(fieldHandle);
                    if (metadata.GetString(field.Name) != "CanonicalJson")
                        continue;
                    var constantHandle = field.GetDefaultValue();
                    if (constantHandle.IsNil)
                        break;
                    var constant = metadata.GetConstant(constantHandle);
                    if (constant.TypeCode != ConstantTypeCode.String)
                        break;
                    var bytes = metadata.GetBlobBytes(constant.Value);
                    return new UTF8Encoding(false, true)
                        .GetBytes(Encoding.Unicode.GetString(bytes).TrimEnd('\0'));
                }
            }
        }

        return null;
    }

    private const string ProviderTypeName =
        "NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2Provider";

    private static void ValidatePrivateDependencyFailure(
        string modulePath,
        string providerTypeName,
        string scratchRoot,
        string failureKind)
    {
        var copyDirectory = Path.Combine(scratchRoot, $"private-dependency-{failureKind}");
        var copiedModule = CopyModuleOutput(modulePath, copyDirectory);
        var dependencyPath = Path.Combine(copyDirectory, "Orders.AsyncBusiness.dll");
        Require(File.Exists(dependencyPath),
            "The standalone OLD artifact did not contain its expected private Orders.AsyncBusiness dependency.");

        if (failureKind == "missing-dependency")
            File.Delete(dependencyPath);
        else
            File.WriteAllBytes(dependencyPath, [0x4E, 0x4C, 0x42, 0x41, 0x44]);

        var exception = CaptureException(() =>
            ManagedAppModuleLease.Load(copiedModule, providerTypeName));
        var expectedException = failureKind == "missing-dependency"
            ? exception is FileNotFoundException
            : exception is BadImageFormatException;
        Require(expectedException && ManagedAppModuleLease.FailedLoadRootCount == 0,
            $"The loader did not reject the OLD module before creating its load context with a {failureKind} private dependency. " +
            $"Observed {exception.GetType().Name}; failed-load roots={ManagedAppModuleLease.FailedLoadRootCount}.");
        Console.WriteLine($"PASS: {failureKind} private app dependency rejected before ALC creation.");
    }

    private static void ValidateIncompatiblePrivateCopy(
        string modulePath,
        string providerTypeName,
        string scratchRoot,
        string incompatibleContractsPath)
    {
        var copyDirectory = Path.Combine(scratchRoot, "incompatible-private-contracts");
        var copiedModule = CopyModuleOutput(modulePath, copyDirectory);
        File.Copy(
            incompatibleContractsPath,
            Path.Combine(copyDirectory, "NetLift.Contracts.dll"),
            overwrite: true);

        var exception = CaptureException(() =>
            ManagedAppModuleLease.Load(copiedModule, providerTypeName));
        Require(exception is FileLoadException &&
                exception.Message.Contains("private copy", StringComparison.OrdinalIgnoreCase),
            "The loader accepted an incompatible private Contracts assembly copy.");
        Console.WriteLine("PASS: valid but incompatible private Contracts assembly copy rejected.");
    }

    private static string CopyModuleOutput(string modulePath, string destinationDirectory)
    {
        if (Directory.Exists(destinationDirectory))
            Directory.Delete(destinationDirectory, recursive: true);
        Directory.CreateDirectory(destinationDirectory);

        var sourceDirectory = Path.GetDirectoryName(modulePath)
            ?? throw new InvalidOperationException("The app module path has no containing directory.");
        foreach (var file in Directory.EnumerateFiles(
                     sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var copyPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(copyPath)!);
            File.Copy(file, copyPath);
        }
        return Path.Combine(destinationDirectory, Path.GetFileName(modulePath));
    }

    private static void RunSchemaMismatch(
        ManagedAppModuleLease moduleLease,
        LiftAsyncCallbackV1HostDescriptor descriptor)
    {
        var mismatchedHash = descriptor.LocalSchemaHash.ToArray();
        mismatchedHash[0] ^= 0x01;
        var mismatchedDescriptor = descriptor with { LocalSchemaHash = mismatchedHash };
        var registry = new AsyncCallbackV1SmokeSupport.AsyncCallbackV1SmokeRegistry(mismatchedDescriptor);
        var outstandingBefore = GeneratedHostRegistration.OutstandingRegistrationCount;
        var status = GeneratedHostRegistration.Register(moduleLease, registry, out var registration);

        Require(status == SchemaMismatch &&
                registration is null &&
                registry.Handler is null &&
                registry.RegistrationCount == 0 &&
                GeneratedHostRegistration.OutstandingRegistrationCount == outstandingBefore &&
                moduleLease.IsReleased,
            "The schema mismatch was not rejected before route publication and lease release.");
        Console.WriteLine("PASS: local schema pin mismatch rejected before route publication.");
    }

    private static void RunCloseFailure(
        ManagedAppModuleLease moduleLease,
        bool isNew,
        LiftAsyncCallbackV1HostDescriptor descriptor,
        ulong endpointToken)
    {
        var registry = new AsyncCallbackV1SmokeSupport.AsyncCallbackV1SmokeRegistry(descriptor);
        var outstandingBefore = GeneratedHostRegistration.OutstandingRegistrationCount;
        var registerStatus = GeneratedHostRegistration.Register(
            moduleLease, registry, out var registration);
        Require(registerStatus == Success && registration is not null,
            "The lifetime-disposal failure fixture did not register its generated route.");
        var activeRegistration = registration
            ?? throw new InvalidOperationException("Successful V2 registration returned no owner.");
        var probe = GetProbe(moduleLease.ModuleAssembly, isNew, endpointToken);
        SetBool(probe, "FailOnDispose", true);

        var closeStatus = activeRegistration.Close();
        var repeatedCloseStatus = activeRegistration.Close();
        Require(closeStatus == ManagedFailure &&
                repeatedCloseStatus == ManagedFailure &&
                !activeRegistration.IsDispatchable &&
                activeRegistration.IsModuleLeaseRetained &&
                !moduleLease.IsReleased &&
                activeRegistration.EndpointToken == descriptor.EndpointToken &&
                registry.Handler is null &&
                registry.RegistrationCount == 1 &&
                GeneratedHostRegistration.OutstandingRegistrationCount == outstandingBefore + 1 &&
                ReadBool(probe, "IsDisposed") &&
                ReadInt(probe, "EndpointDisposalCount") == 1 &&
                ReadInt(probe, "AcquisitionCount") == 0 &&
                ReadInt(probe, "DisposalCount") == 0,
            "Failed bridge close did not retain a non-dispatchable module/endpoint association.");
        Console.WriteLine(
            $"PASS: {(isNew ? "NEW" : "OLD")} attached lifetime disposal failure retained the " +
            "non-dispatchable module lease and synthetic endpoint-token association.");
    }

    private static async Task RunGeneratedRouteAsync(
        ManagedAppModuleLease moduleLease,
        bool isNew,
        ulong endpointToken,
        LiftAsyncCallbackV1HostDescriptor descriptor)
    {
        var registry = new AsyncCallbackV1SmokeSupport.AsyncCallbackV1SmokeRegistry(descriptor);
        var outstandingBefore = GeneratedHostRegistration.OutstandingRegistrationCount;
        var registerStatus = GeneratedHostRegistration.Register(
            moduleLease, registry, out var registration);
        Require(registerStatus == Success && registration is not null &&
                registration.IsDispatchable &&
                registration.IsModuleLeaseRetained &&
                GeneratedHostRegistration.OutstandingRegistrationCount == outstandingBefore + 1,
            "The dynamically loaded generated module did not register a rooted host route.");
        var activeRegistration = registration
            ?? throw new InvalidOperationException("Successful V2 registration returned no owner.");

        var probe = GetProbe(moduleLease.ModuleAssembly, isNew, endpointToken);
        await DispatchAndAssertAsync(
            registry,
            CreateInvocation(isNew, !isNew, endpointToken, 1, true),
            isNew,
            peerIsNew: !isNew,
            operationId: 1,
            confirmationAccepted: true);
        await DispatchAndAssertAsync(
            registry,
            CreateInvocation(isNew, !isNew, endpointToken, 2, false),
            isNew,
            peerIsNew: !isNew,
            operationId: 2,
            confirmationAccepted: false);

        var pendingInvocation = CreateInvocation(isNew, !isNew, endpointToken, 3, true);
        pendingInvocation.NativeInvoker.DeferredReply =
            new TaskCompletionSource<LiftNativeReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingTask = InvokeRouteAsync(
            registry, pendingInvocation, isNew, peerIsNew: !isNew, operationId: 3);
        await pendingInvocation.NativeInvoker.CallEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Require(activeRegistration.Close() == Busy &&
                activeRegistration.IsModuleLeaseRetained &&
                !moduleLease.IsReleased &&
                !activeRegistration.IsDispatchable,
            "Busy close did not retain the module lease while an operation was in flight.");

        var lateInvocation = CreateInvocation(isNew, !isNew, endpointToken, 4, true);
        await ExpectFailureAsync(
            registry.InvokeAsync(
                EncodeRequest(peerIsNew: !isNew, operationId: 4),
                WithOperation(lateInvocation.Context, endpointToken, 4),
                CancellationToken.None).AsTask(),
            "A new route entered after close had begun.");
        Require(ReadInt(probe, "AcquisitionCount") == 3 &&
                ReadInt(probe, "DisposalCount") == 2,
            "A refused start acquired an app operation lease or an in-flight lease was released early.");

        pendingInvocation.NativeInvoker.DeferredReply!.SetResult(
            new LiftNativeReply(
                LiftNativeReplyKind.Result, new byte[] { 1 }, ReadOnlyMemory<byte>.Empty));
        var pendingResult = await pendingTask;
        AssertResult(pendingResult, isNew, peerIsNew: !isNew, operationId: 3, accepted: true);

        Require(ReadInt(probe, "AcquisitionCount") == 3 &&
                ReadInt(probe, "DisposalCount") == 3 &&
                activeRegistration.Close() == Success &&
                ReadBool(probe, "IsDisposed") &&
                ReadInt(probe, "EndpointDisposalCount") == 1 &&
                !activeRegistration.IsModuleLeaseRetained &&
                moduleLease.IsReleased &&
                GeneratedHostRegistration.OutstandingRegistrationCount == outstandingBefore,
            "Successful drain/close did not dispose exact leases and release the module root once.");

        Console.WriteLine(
            $"PASS: { (isNew ? "NEW" : "OLD") } custom-ALC generated route, typed Task<bool> callback, " +
            "Busy refusal, drain, close, and zero outstanding operation leases.");
    }

    private static Task RunRollbackQuarantineAsync(
        ManagedAppModuleLease moduleLease,
        bool isNew,
        LiftAsyncCallbackV1HostDescriptor descriptor,
        ulong endpointToken)
    {
        unsafe
        {
            var api = RegisterThenFailForTest.ReplaceCallback(moduleLease.Api);
            var registry = new AsyncCallbackV1SmokeSupport.AsyncCallbackV1SmokeRegistry(
                descriptor, failRouteDisposal: true);
            var before = GeneratedHostRegistration.OutstandingRegistrationCount;

            var status = GeneratedHostRegistration.RegisterForTesting(
                moduleLease, registry, api, out var registration);
            Require(status == ManagedFailure &&
                    registration is not null &&
                    !registration.IsDispatchable &&
                    registration.IsModuleLeaseRetained &&
                    GeneratedHostRegistration.OutstandingRegistrationCount == before + 1,
                "A failed partial rollback did not retain a non-dispatchable module quarantine.");
            var quarantinedRegistration = registration
                ?? throw new InvalidOperationException("Failed rollback lost its quarantine owner.");

            var probe = GetProbe(moduleLease.ModuleAssembly, isNew, endpointToken);
            Require(ReadInt(probe, "AcquisitionCount") == 0 &&
                    ReadInt(probe, "DisposalCount") == 0 &&
                    quarantinedRegistration.IsModuleLeaseRetained &&
                    !moduleLease.IsReleased &&
                    GeneratedHostRegistration.OutstandingRegistrationCount == before + 1 &&
                    registry.Handler is not null &&
                    ReadInt(probe, "EndpointDisposalCount") == 0,
                "An uncertain partial rollback did not retain a non-dispatchable module quarantine.");

            Console.WriteLine(
                "PASS: failed partial rollback remained quarantined with the provider and table roots retained.");
            return Task.CompletedTask;
        }
    }

    private static async Task DispatchAndAssertAsync(
        AsyncCallbackV1SmokeSupport.AsyncCallbackV1SmokeRegistry registry,
        AsyncCallbackV1SmokeInvocation invocation,
        bool localIsNew,
        bool peerIsNew,
        int operationId,
        bool confirmationAccepted)
    {
        var payload = EncodeRequest(peerIsNew, operationId);
        var result = await registry.InvokeAsync(
            payload,
            WithOperation(invocation.Context, invocation.Descriptor.EndpointToken, operationId),
            CancellationToken.None);
        AssertResult(result, localIsNew, peerIsNew, operationId, confirmationAccepted);
        Require(invocation.NativeInvoker.CallCount == 1 &&
                invocation.NativeInvoker.LastServiceId == 30 &&
                invocation.NativeInvoker.LastMethodId == 1 &&
                invocation.NativeInvoker.LastInvocationContext.OperationToken ==
                EndpointOperationToken(invocation.Descriptor.EndpointToken, operationId),
            "The generated Task<bool> callback lost typed route identity.");
    }

    private static async Task<ReadOnlyMemory<byte>> InvokeRouteAsync(
        AsyncCallbackV1SmokeSupport.AsyncCallbackV1SmokeRegistry registry,
        AsyncCallbackV1SmokeInvocation invocation,
        bool localIsNew,
        bool peerIsNew,
        int operationId) =>
        await registry.InvokeAsync(
            EncodeRequest(peerIsNew, operationId),
            WithOperation(invocation.Context, invocation.Descriptor.EndpointToken, operationId),
            CancellationToken.None);

    private static void AssertResult(
        ReadOnlyMemory<byte> encodedResult,
        bool localIsNew,
        bool peerIsNew,
        int operationId,
        bool accepted)
    {
        var expectedCode = operationId;
        if (localIsNew)
        {
            var result = AsyncCallbackV1SmokeSupport.DecodeNewResult(encodedResult);
            Require(result.Code == expectedCode && result.Accepted == accepted &&
                    result.Audited == peerIsNew,
                "The NEW generated Task<Result> route returned an incorrect typed result.");
        }
        else
        {
            var result = AsyncCallbackV1SmokeSupport.DecodeOldResult(encodedResult);
            Require(result.Code == expectedCode && result.Accepted == accepted,
                "The OLD generated Task<Result> route returned an incorrect typed result.");
        }
    }

    private static ReadOnlyMemory<byte> EncodeRequest(bool peerIsNew, int operationId) =>
        peerIsNew
            ? AsyncCallbackV1SmokeSupport.EncodeNewRequest(
                operationId == 2 ? "decline" : "approve",
                operationId,
                $"module-{operationId}")
            : AsyncCallbackV1SmokeSupport.EncodeOldRequest(
                operationId == 2 ? "decline" : "approve",
                operationId);

    private static AsyncCallbackV1SmokeInvocation CreateInvocation(
        bool localIsNew,
        bool peerIsNew,
        ulong endpointToken,
        int operationId,
        bool confirmationAccepted)
    {
        var invocation = AsyncCallbackV1SmokeSupport.CreateInvocation(
            localIsNew,
            CancellationToken.None,
            confirmationAccepted,
            endpointToken,
            peerIsNew);
        return invocation with
        {
            Context = WithOperation(invocation.Context, endpointToken, operationId)
        };
    }

    private static LiftInvocationContext WithOperation(
        LiftInvocationContext context,
        ulong endpointToken,
        int operationId) =>
        context with
        {
            EndpointToken = endpointToken,
            RegistrationToken = endpointToken + 2,
            RegistrationGeneration = 1,
            CorrelationToken = EndpointOperationToken(endpointToken, operationId) + 10_000,
            OperationToken = EndpointOperationToken(endpointToken, operationId)
        };

    private static ulong EndpointOperationToken(ulong endpointToken, int operationId) =>
        endpointToken * 100 + (ulong)operationId;

    private static async Task ExpectFailureAsync(Task task, string failureMessage)
    {
        try
        {
            await task;
        }
        catch (LiftAsyncCallbackV1ContractException)
        {
            return;
        }

        throw new InvalidOperationException(failureMessage);
    }

    private static object GetProbe(Assembly moduleAssembly, bool isNew, ulong endpointToken)
    {
        var typeName = isNew
            ? "NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModule"
            : "NetLift.Negotiation.AsyncHost.Managed.AsyncCallbackV1ManagedHostModuleV2";
        var moduleType = moduleAssembly.GetType(typeName, throwOnError: true)!;
        var probe = moduleType.GetMethod("GetV2Probe", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [endpointToken]);
        return probe ?? throw new InvalidOperationException("The app module did not expose its lease probe.");
    }

    private static int ReadInt(object target, string propertyName) =>
        (int)target.GetType().GetProperty(propertyName)!.GetValue(target)!;

    private static bool ReadBool(object target, string propertyName) =>
        (bool)target.GetType().GetProperty(propertyName)!.GetValue(target)!;

    private static void SetBool(object target, string propertyName, bool value) =>
        target.GetType().GetProperty(propertyName)!.SetValue(target, value);

    private static bool IsLoadedInDefaultContext(string assemblyName) =>
        AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
            AssemblyLoadContext.GetLoadContext(assembly) == AssemblyLoadContext.Default &&
            string.Equals(
                assembly.GetName().Name,
                assemblyName,
                StringComparison.OrdinalIgnoreCase));

    private static void ValidateContractReferencePreflight(
        string modulePath,
        string providerTypeName,
        string scratchRoot,
        string moduleName)
    {
        var hostContractsName = typeof(ILiftAsyncCallbackV1ManagedHostModuleProviderV2).Assembly.GetName();
        var mismatchedReference = new AssemblyName(hostContractsName.FullName)
        {
            Version = new Version(
                hostContractsName.Version!.Major + 1,
                hostContractsName.Version.Minor,
                hostContractsName.Version.Build,
                hostContractsName.Version.Revision)
        };
        Require(!ManagedAppModuleLease.HasExactIdentity(mismatchedReference, hostContractsName),
            "Contracts identity preflight accepted an incompatible AssemblyRef version.");

        var moduleDirectory = Path.GetDirectoryName(modulePath)!;
        var scratchDirectory = Path.Combine(scratchRoot, $"tampered-{moduleName}");
        if (Directory.Exists(scratchDirectory))
            Directory.Delete(scratchDirectory, recursive: true);
        Directory.CreateDirectory(scratchDirectory);

        try
        {
            foreach (var file in Directory.EnumerateFiles(
                         moduleDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(moduleDirectory, file);
                var copyPath = Path.Combine(scratchDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(copyPath)!);
                File.Copy(file, copyPath);
            }

            var copiedContracts = Path.Combine(scratchDirectory, "NetLift.Contracts.dll");
            File.AppendAllText(copiedContracts, "tampered-private-contract");
            var copiedModule = Path.Combine(scratchDirectory, Path.GetFileName(modulePath));
            var exception = CaptureException(() =>
                ManagedAppModuleLease.Load(copiedModule, providerTypeName));
            Require(exception is FileLoadException &&
                    exception.Message.Contains("private copy", StringComparison.OrdinalIgnoreCase),
                "The loader silently bound a tampered private Contracts copy to the host assembly.");
        }
        finally
        {
            Directory.Delete(scratchDirectory, recursive: true);
        }
    }

    private static Exception CaptureException(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            return exception;
        }
        throw new InvalidOperationException("Expected loader preflight to reject a tampered module.");
    }

    private static unsafe class RegisterThenFailForTest
    {
        private static delegate* unmanaged[Cdecl]<nint, int> _innerRegisterHost;

        public static unsafe LiftAsyncCallbackV1GeneratedHostModuleApiV2 ReplaceCallback(
            LiftAsyncCallbackV1GeneratedHostModuleApiV2 api)
        {
            _innerRegisterHost = api.RegisterHost;
            api.RegisterHost = &RegisterThenFail;
            return api;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int RegisterThenFail(nint contextHandle)
        {
            var status = _innerRegisterHost(contextHandle);
            return status == Success
                ? (int)LiftAsyncCallbackV1ManagedBridgeStatusV2.RegistrationFailure
                : status;
        }
    }

    private sealed class TestEndpointBindingProvider(
        LiftAsyncCallbackV1HostDescriptor descriptor,
        AsyncCallbackV1SmokeSupport.AsyncCallbackV1SmokeRegistry registry,
        TestResultContextLease resultContext) : IManagedAppEndpointBindingProvider
    {
        public int AcquisitionCount { get; private set; }

        public ManagedAppEndpointBindingLease? Acquire(ulong endpointToken)
        {
            AcquisitionCount++;
            return endpointToken == descriptor.EndpointToken
                ? new ManagedAppEndpointBindingLease(
                    descriptor, new TestManagedAppEndpointHostRegistry(registry), resultContext)
                : null;
        }
    }

    private sealed class TestManagedAppEndpointHostRegistry(
        AsyncCallbackV1SmokeSupport.AsyncCallbackV1SmokeRegistry inner)
        : IManagedAppEndpointHostRegistry
    {
        private bool _dispatchEnabled;
        private bool _draining;

        public LiftAsyncCallbackV1HostDescriptor Descriptor => inner.Descriptor;

        public bool HasRegisteredRoute => inner.Handler is not null;

        public int ActiveRouteLeaseCount => 0;

        public bool EnableRouteDispatch()
        {
            if (_draining || !HasRegisteredRoute || _dispatchEnabled)
                return false;
            _dispatchEnabled = true;
            return true;
        }

        public void BeginRouteDrain() => _draining = true;

        public bool TryGetHandler(out LiftAsyncCallbackV1ManagedRouteHandler handler)
        {
            if (!_draining && _dispatchEnabled && inner.Handler is { } registered)
            {
                handler = registered;
                return true;
            }

            handler = null!;
            return false;
        }

        public IDisposable RegisterManagedRoute(
            uint serviceId,
            uint methodId,
            LiftAsyncCallbackV1ManagedRouteHandler handler) =>
            inner.RegisterManagedRoute(serviceId, methodId, handler);
    }

    private sealed class TestResultContextLease : IDisposable
    {
        public int DisposeCount { get; private set; }
        public int FailuresRemaining { get; set; }
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("Injected result-context disposal failure.");
            }
            IsDisposed = true;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
