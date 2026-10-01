using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using NetLift;
using NetLift.Generator;

internal static class HostModuleTests
{
    private const string ContractSource = """
        namespace HostFixture
        {
            [LiftDto("host-input", Id = 71)]
            public sealed record HostInput([property: LiftField(1)] int Value);

            [LiftDto("host-output", Id = 72)]
            public sealed record HostOutput([property: LiftField(1)] int Value);

            [LiftService("host-service", Id = 70)]
            public interface IHostService
            {
                [LiftMethod(2)]
                HostOutput Echo(HostInput input);

                [LiftMethod(5)]
                Task<HostOutput> EchoAsync(HostInput input, CancellationToken cancellationToken);
            }

            [LiftNativeService("confirmation", Id = 73, ReplyProtocol = LiftNativeReplyProtocol.TypedV1)]
            public interface IConfirmation
            {
                [LiftMethod(1)]
                Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken);
            }

            public static class Probe
            {
                public static int FactoryObjectCalls;
                public static int ServiceFactoryCalls;
                public static ulong LastCorrelationToken;
                public static bool ReturnNullFactory;
                public static bool ThrowFactory;
                public static bool ThrowNativeFailure;
                public static bool ThrowMalformedNativeFailure;
                public static bool ThrowInvalidUnsupportedDiagnostic;
                public static bool ThrowOversizedUnavailableDiagnostic;
                public static bool CancelAsync;

                public static void Reset()
                {
                    FactoryObjectCalls = 0;
                    ServiceFactoryCalls = 0;
                    LastCorrelationToken = 0;
                    ReturnNullFactory = false;
                    ThrowFactory = false;
                    ThrowNativeFailure = false;
                    ThrowMalformedNativeFailure = false;
                    ThrowInvalidUnsupportedDiagnostic = false;
                    ThrowOversizedUnavailableDiagnostic = false;
                    CancelAsync = false;
                }
            }

            internal sealed class ServiceFactories : NetLift.Generated.ILiftServiceFactories
            {
                public IHostService CreateService_70(LiftInvocationContext context)
                {
                    Probe.ServiceFactoryCalls++;
                    Probe.LastCorrelationToken = context.CorrelationToken;
                    if (Probe.ThrowFactory)
                        throw new System.InvalidOperationException("factory failed");
                    return new HostService(context);
                }
            }

            internal sealed class HostService(LiftInvocationContext context) : IHostService
            {
                public HostOutput Echo(HostInput input) => new(input.Value + 1);

                public Task<HostOutput> EchoAsync(HostInput input, CancellationToken cancellationToken)
                {
                    if (Probe.ThrowNativeFailure)
                        throw new LiftNativeServiceException(13, "native handler failed",
                            new byte[] { 110, 97, 116, 105, 118, 101 });
                    if (Probe.ThrowMalformedNativeFailure)
                        throw new LiftNativeServiceException(13, "bad diagnostic", new byte[] { 0xFF });
                    if (Probe.ThrowInvalidUnsupportedDiagnostic)
                        throw new LiftNativeServiceException(3, "bad diagnostic", new byte[] { 0xFF });
                    if (Probe.ThrowOversizedUnavailableDiagnostic)
                        throw new LiftNativeServiceException(11, "large diagnostic",
                            new byte[LiftNativeReplyDecoder.MaxDiagnosticBytes + 1]);
                    if (Probe.CancelAsync)
                        throw new OperationCanceledException(cancellationToken);
                    return Task.FromResult(new HostOutput(input.Value + (int)(context.CorrelationToken % 1000)));
                }
            }
        }
        """;

    private const string HostModuleSource = """
        #nullable enable
        using System;
        using NetLift;
        using System.Threading;
        using System.Threading.Tasks;
        [assembly: NetLift.LiftHostModule]

        namespace NetLift.Generated
        {
            public static partial class LiftApplicationBootstrap
            {
                internal static partial ILiftServiceFactories? CreateServiceFactories()
                {
                    HostFixture.Probe.FactoryObjectCalls++;
                    if (HostFixture.Probe.ThrowFactory)
                        throw new System.InvalidOperationException("factory creation failed");
                    return HostFixture.Probe.ReturnNullFactory ? null : new HostFixture.ServiceFactories();
                }
            }
        }
        """ + ContractSource;

    private const string V4HostModuleSource = """
        #nullable enable
        using System;
        using System.Collections.Generic;
        using System.Runtime.CompilerServices;
        using System.Threading;
        using System.Threading.Tasks;
        using NetLift;
        [assembly: NetLift.LiftHostModule]

        namespace NetLift.Generated
        {
            public static partial class LiftApplicationBootstrap
            {
                internal static partial ILiftServiceFactories? CreateServiceFactories()
                {
                    HostFixtureV4.Probe.FactoryObjectCalls++;
                    return new HostFixtureV4.ServiceFactories();
                }
            }
        }

        namespace HostFixtureV4
        {
            [LiftDto("order-draft", Id = 81)]
            public sealed record OrderDraft([property: LiftField(1)] int Value);

            [LiftDto("order-receipt", Id = 82)]
            public sealed record OrderReceipt([property: LiftField(1)] int Value);

            [LiftDto("order-update", Id = 83)]
            public sealed record OrderUpdate([property: LiftField(1)] int Value);

            [LiftService("transform", Id = 69)]
            public interface ITransformService
            {
                [LiftMethod(1)]
                OrderDraft Transform(OrderDraft value);
            }

            [LiftService("orders", Id = 70)]
            public interface IOrdersService
            {
                [LiftMethod(1)]
                Task<OrderReceipt> PlaceAsync(OrderDraft draft, CancellationToken cancellationToken);

                [LiftStream(2)]
                IAsyncEnumerable<OrderUpdate> WatchAsync(
                    [EnumeratorCancellation] CancellationToken cancellationToken);
            }

            [LiftNativeService("confirmation", Id = 73, ReplyProtocol = LiftNativeReplyProtocol.TypedV1)]
            public interface IConfirmationService
            {
                [LiftMethod(1)]
                Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken);
            }

            public static class Probe
            {
                public static int FactoryObjectCalls;
                public static int OrdersFactoryCalls;
                public static int TransformFactoryCalls;
                public static int WatchCalls;
                public static bool ThrowFromWatch;
                public static ulong WatchCorrelationToken;
                public static readonly System.Collections.Concurrent.ConcurrentQueue<ulong> NativeCorrelationTokens = new();
                public static IOrdersService? LastPlaceService;
                public static IOrdersService? LastWatchService;

                public static void Reset()
                {
                    FactoryObjectCalls = 0;
                    OrdersFactoryCalls = 0;
                    TransformFactoryCalls = 0;
                    WatchCalls = 0;
                    ThrowFromWatch = false;
                    WatchCorrelationToken = 0;
                    LastPlaceService = null;
                    LastWatchService = null;
                    while (NativeCorrelationTokens.TryDequeue(out _)) { }
                }
            }

            internal sealed class ServiceFactories : NetLift.Generated.ILiftServiceFactories
            {
                private readonly IOrdersService _orders = new OrdersService(new ConfirmationService());
                private readonly ITransformService _transform = new TransformService();

                public IOrdersService CreateService_70()
                {
                    Probe.OrdersFactoryCalls++;
                    return _orders;
                }

                public ITransformService CreateService_69()
                {
                    Probe.TransformFactoryCalls++;
                    return _transform;
                }
            }

            internal sealed class TransformService : ITransformService
            {
                public OrderDraft Transform(OrderDraft value) => new(value.Value + 1);
            }

            internal interface IConfirmation
            {
                Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken);
            }

            internal sealed class ConfirmationService : IConfirmation
            {
                public async Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken)
                {
                    var context = LiftInvocationContextAccessor.Current;
                    Probe.NativeCorrelationTokens.Enqueue(context.CorrelationToken);
                    var reply = await context.NativeServices.InvokeAsync(
                        73, 1, System.Text.Encoding.UTF8.GetBytes(prompt),
                        context.CorrelationToken, cancellationToken);
                    return LiftNativeReplyDecoder.GetBooleanResult(reply, cancellationToken);
                }
            }

            internal sealed class OrdersService(IConfirmation confirmation) : IOrdersService
            {
                private int _lastReceipt;

                public async Task<OrderReceipt> PlaceAsync(
                    OrderDraft draft,
                    CancellationToken cancellationToken)
                {
                    Probe.LastPlaceService = this;
                    if (!await confirmation.ConfirmAsync(draft.Value.ToString(), cancellationToken))
                        throw new InvalidOperationException("Order was declined.");
                    return new OrderReceipt(Interlocked.Increment(ref _lastReceipt));
                }

                public async IAsyncEnumerable<OrderUpdate> WatchAsync(
                    [EnumeratorCancellation] CancellationToken cancellationToken)
                {
                    Probe.LastWatchService = this;
                    Probe.WatchCorrelationToken = LiftInvocationContextAccessor.Current.CorrelationToken;
                    Probe.WatchCalls++;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Probe.ThrowFromWatch)
                        throw new InvalidOperationException("WatchAsync buffer failed.");
                    yield return new OrderUpdate(_lastReceipt);
                    await Task.CompletedTask.ConfigureAwait(false);
                }
            }
        }
        """;

    public static void Run()
    {
        var (generated, compilation, diagnostics) = Generate(HostModuleSource, "NetLiftHostModuleFixture");
        var (generatedAgain, _, _) = Generate(HostModuleSource, "NetLiftHostModuleFixtureAgain");
        Require(!diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) &&
                !compilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
            "Host module fixture failed to compile: " + FormatErrors(diagnostics, compilation.GetDiagnostics()));
        Require(generated["LiftHostModule.g.cs"] == generatedAgain["LiftHostModule.g.cs"] &&
                generated["LiftContract.g.cs"] == generatedAgain["LiftContract.g.cs"],
            "Host module source generation was nondeterministic.");

        var baseline = Generate(
            """
            #nullable enable
            using System;
            using NetLift;
            using System.Threading;
            using System.Threading.Tasks;
            """ + ContractSource,
            "NetLiftHostModuleBaseline").GeneratedSources;
        var schemaText = ReadSchema(generated["LiftContract.g.cs"]);
        using var schema = JsonDocument.Parse(schemaText);
        using var baselineSchema = JsonDocument.Parse(ReadSchema(baseline["LiftContract.g.cs"]));
        var schemaHash = schema.RootElement.GetProperty("schemaHash").GetString()!;
        Require(schema.RootElement.GetProperty("schemaVersion").GetInt32() == 3 &&
                baselineSchema.RootElement.GetProperty("schemaHash").GetString() == schemaHash,
            "The host-module opt-in changed the v3 schema version or hash.");
        Require(generated["LiftHostModule.g.cs"].Contains(
                    "DescribeV1(global::NetLift.LiftContractDescriptorV1* output, uint outputCapacity)",
                    StringComparison.Ordinal) &&
                generated["LiftHostModule.g.cs"].Contains("RegisterV1()", StringComparison.Ordinal) &&
                generated["LiftHostModule.g.cs"].Contains("Dispatch_70_2", StringComparison.Ordinal) &&
                generated["LiftHostModule.g.cs"].Contains("Dispatch_70_5", StringComparison.Ordinal),
            "The generated descriptor, stable bootstrap functions, or numeric route handlers are missing.");
        VerifyIncrementalCache();

        var missingFactory = Generate(
            """
            #nullable enable
            using System;
            using NetLift;
            using System.Threading;
            using System.Threading.Tasks;
            [assembly: NetLift.LiftHostModule]
            """ + ContractSource,
            "NetLiftMissingHostFactoryFixture");
        Require(missingFactory.Diagnostics.Any(diagnostic => diagnostic.Id == "NLT003"),
            "The host opt-in without CreateServiceFactories did not produce NLT003.");
        var unsupportedShape = Generate(
            HostModuleSource.Replace(
                "HostOutput Echo(HostInput input);",
                "int Echo(int input);",
                StringComparison.Ordinal),
            "NetLiftUnsupportedHostShapeFixture");
        Require(unsupportedShape.Diagnostics.Any(diagnostic =>
                diagnostic.Id == "NLT002" &&
                diagnostic.GetMessage().Contains("Opted-in host modules currently support", StringComparison.Ordinal)),
            "An unsupported host method shape was omitted instead of diagnosed.");

        using var assemblyStream = new MemoryStream();
        var emit = compilation.Emit(assemblyStream);
        Require(emit.Success, "Host module assembly failed to emit: " +
            string.Join(Environment.NewLine, emit.Diagnostics
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
        assemblyStream.Position = 0;
        var assembly = AssemblyLoadContext.Default.LoadFromStream(assemblyStream);
        var bootstrap = assembly.GetType("NetLift.Generated.AndroidContractBootstrap", throwOnError: true)!;
        var describe = (DescribeV1Delegate)bootstrap.GetMethod("DescribeV1")!
            .CreateDelegate(typeof(DescribeV1Delegate));
        var register = (Func<uint>)bootstrap.GetMethod("RegisterV1")!.CreateDelegate(typeof(Func<uint>));

        var requiredFeatures = (uint)(LiftHostFeatureBits.Sync |
            LiftHostFeatureBits.AsyncCallbacks | LiftHostFeatureBits.TypedNativeReply);
        ValidateDescriptor(describe, schemaHash, requiredFeatures);

        var probe = assembly.GetType("HostFixture.Probe", throwOnError: true)!;
        ResetProbe(probe);
        Require(register() == (uint)LiftBootstrapStatus.NotReady,
            "RegisterV1 did not fail when the process registry was unavailable.");

        var hashBytes = Convert.FromHexString(schemaHash);
        RejectBeforeFactory(register, probe,
            new FakeDispatchRegistry(hashBytes, requiredFeatures, descriptorVersion: 2),
            LiftBootstrapStatus.DescriptorVersionMismatch);
        RejectBeforeFactory(register, probe,
            new FakeDispatchRegistry(hashBytes, requiredFeatures, abiVersion: 2),
            LiftBootstrapStatus.AbiMismatch);
        RejectBeforeFactory(register, probe,
            new FakeDispatchRegistry(new byte[32], requiredFeatures),
            LiftBootstrapStatus.SchemaMismatch);
        RejectBeforeFactory(register, probe,
            new FakeDispatchRegistry(hashBytes, requiredFeatures & ~0x4u),
            LiftBootstrapStatus.FeatureMismatch);
        ResetProbe(probe);
        var throwingValidation = new FakeDispatchRegistry(hashBytes, requiredFeatures)
        {
            ThrowDuringValidation = true
        };
        Require(RegisterWith(register, throwingValidation) == (uint)LiftBootstrapStatus.ManagedFailure &&
                GetProbe<int>(probe, "FactoryObjectCalls") == 0 &&
                throwingValidation.RouteCount == 0,
            "RegisterV1 allowed a registry validation exception to escape or reach factory creation.");

        SetProbe(probe, "ReturnNullFactory", true);
        var noFactories = new FakeDispatchRegistry(hashBytes, requiredFeatures);
        Require(RegisterWith(register, noFactories) == (uint)LiftBootstrapStatus.FactoryUnavailable &&
                noFactories.RouteCount == 0,
            "RegisterV1 published routes without an app factory.");
        ResetProbe(probe);

        SetProbe(probe, "ThrowFactory", true);
        var throwingFactory = new FakeDispatchRegistry(hashBytes, requiredFeatures);
        Require(RegisterWith(register, throwingFactory) == (uint)LiftBootstrapStatus.ManagedFailure &&
                throwingFactory.RouteCount == 0,
            "RegisterV1 leaked a factory exception or published partial routes.");
        ResetProbe(probe);

        var registry = new FakeDispatchRegistry(hashBytes, requiredFeatures);
        Require(RegisterWith(register, registry) == (uint)LiftBootstrapStatus.Success &&
                registry.RouteCount == 2 && registry.Contains(70, 2) && registry.Contains(70, 5),
            "RegisterV1 did not publish its complete generated route batch.");
        var originalRouteCount = registry.RouteCount;
        Require(RegisterWith(register, registry) == (uint)LiftBootstrapStatus.RegistrationConflict &&
                registry.RouteCount == originalRouteCount,
            "Duplicate registration replaced or partially changed existing routes.");

        var context = new LiftInvocationContext(CancellationToken.None, 0x10203040ul, new FakeNativeInvoker());
        var payload = EncodeIntDto(41);
        var syncResult = Invoke(registry, 70, 2, payload, context);
        Require(syncResult.Status == LiftDispatchStatus.Success &&
                syncResult.Payload.Span.SequenceEqual(EncodeIntDto(42)) &&
                GetProbe<ulong>(probe, "LastCorrelationToken") == context.CorrelationToken,
            "Generated sync ID-to-codec-to-direct-dispatch lost its result or per-call context.");
        Require(GetProbe<int>(probe, "ServiceFactoryCalls") == 1,
            "Generated dispatch did not resolve the service once per valid operation.");

        var malformed = Invoke(registry, 70, 2, new byte[] { 1, 2 }, context);
        Require(malformed.Status == LiftDispatchStatus.MalformedPayload,
            "Generated dispatch converted malformed input into success.");

        SetProbe(probe, "ThrowFactory", true);
        var factoryFailure = Invoke(registry, 70, 2, payload, context);
        SetProbe(probe, "ThrowFactory", false);
        Require(factoryFailure.Status == LiftDispatchStatus.ManagedFailure,
            "A service-factory exception became a successful dispatch.");

        SetProbe(probe, "ThrowNativeFailure", true);
        var handlerFailure = Invoke(registry, 70, 5, payload, context);
        SetProbe(probe, "ThrowNativeFailure", false);
        Require(handlerFailure.Status == LiftDispatchStatus.HandlerFailure &&
                handlerFailure.DiagnosticUtf8.Span.SequenceEqual(new byte[] { 110, 97, 116, 105, 118, 101 }),
            "Generated async dispatch lost typed handler failure status or diagnostic.");

        SetProbe(probe, "ThrowMalformedNativeFailure", true);
        var malformedFailure = Invoke(registry, 70, 5, payload, context);
        SetProbe(probe, "ThrowMalformedNativeFailure", false);
        Require(malformedFailure.Status == LiftDispatchStatus.MalformedPayload,
            "Generated dispatch accepted a malformed or unbounded native handler diagnostic.");

        SetProbe(probe, "ThrowInvalidUnsupportedDiagnostic", true);
        var invalidUnsupportedDiagnostic = Invoke(registry, 70, 5, payload, context);
        SetProbe(probe, "ThrowInvalidUnsupportedDiagnostic", false);
        Require(invalidUnsupportedDiagnostic.Status == LiftDispatchStatus.MalformedPayload,
            "Generated dispatch propagated invalid UTF-8 diagnostics on a non-handler-failure status.");

        SetProbe(probe, "ThrowOversizedUnavailableDiagnostic", true);
        var oversizedUnavailableDiagnostic = Invoke(registry, 70, 5, payload, context);
        SetProbe(probe, "ThrowOversizedUnavailableDiagnostic", false);
        Require(oversizedUnavailableDiagnostic.Status == LiftDispatchStatus.MalformedPayload,
            "Generated dispatch propagated oversized diagnostics on a non-handler-failure status.");

        SetProbe(probe, "CancelAsync", true);
        var cancelled = Invoke(registry, 70, 5, payload,
            context with { CancellationToken = new CancellationToken(true) });
        SetProbe(probe, "CancelAsync", false);
        Require(cancelled.Status == LiftDispatchStatus.Cancelled,
            "Generated async dispatch converted cancellation into success.");
        Require(GetProbe<int>(probe, "ServiceFactoryCalls") == 7,
            "The per-operation service factory was not called exactly once for each valid request.");

        VerifyNativeOutcomes();

        var failedBatch = new FakeDispatchRegistry(hashBytes, requiredFeatures)
        {
            BatchStatus = LiftBootstrapStatus.RegistrationConflict
        };
        Require(RegisterWith(register, failedBatch) == (uint)LiftBootstrapStatus.RegistrationConflict &&
                failedBatch.RouteCount == 0,
            "A failed registration batch partially published routes.");
        var throwingBatch = new FakeDispatchRegistry(hashBytes, requiredFeatures)
        {
            ThrowDuringBatch = true
        };
        Require(RegisterWith(register, throwingBatch) == (uint)LiftBootstrapStatus.ManagedFailure &&
                throwingBatch.RouteCount == 0,
            "RegisterV1 allowed a batch exception to escape or publish routes.");

        VerifyV4StreamHostModule();
    }

    private static void VerifyV4StreamHostModule()
    {
        var (generated, compilation, diagnostics) = Generate(V4HostModuleSource, "NetLiftV4HostModuleFixture");
        var (generatedAgain, _, _) = Generate(V4HostModuleSource, "NetLiftV4HostModuleFixtureAgain");
        Require(!diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) &&
                !compilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
            "V4 stream host module fixture failed to compile: " +
            FormatErrors(diagnostics, compilation.GetDiagnostics()));
        Require(generated["LiftHostModule.g.cs"] == generatedAgain["LiftHostModule.g.cs"] &&
                generated["LiftContract.g.cs"] == generatedAgain["LiftContract.g.cs"],
            "V4 stream host generation was nondeterministic.");
        using var schema = JsonDocument.Parse(ReadSchema(generated["LiftContract.g.cs"]));
        var schemaRoot = schema.RootElement;
        var schemaHash = schemaRoot.GetProperty("schemaHash").GetString()!;
        Require(schemaRoot.GetProperty("schemaVersion").GetInt32() == 4 &&
                generated["LiftHostModule.g.cs"].Contains("RegisterV4()", StringComparison.Ordinal) &&
                !generated["LiftHostModule.g.cs"].Contains("RegisterV1()", StringComparison.Ordinal) &&
                generated["LiftHostModule.g.cs"].Contains("TryRegisterV4Batch", StringComparison.Ordinal) &&
                generated["LiftHostModule.g.cs"].Contains("LiftStreamSubscriptionRegistration", StringComparison.Ordinal) &&
                generated["LiftHostModule.g.cs"].Contains("RequiredFeatureBits = 15u", StringComparison.Ordinal),
            "V4 host output did not select RegisterV4 or publish the expected additive stream table.");

        var unsupportedEventSource = V4HostModuleSource.Replace(
            "            [EnumeratorCancellation] CancellationToken cancellationToken);\n    }\n\n    [LiftNativeService(\"confirmation\"",
            "            [EnumeratorCancellation] CancellationToken cancellationToken);\n\n    [LiftEvent(3)] event Action<OrderUpdate>? Updated;\n    }\n\n    [LiftNativeService(\"confirmation\"",
            StringComparison.Ordinal);
        var (_, _, eventDiagnostics) = Generate(unsupportedEventSource, "NetLiftV4EventUnsupportedFixture");
        Require(eventDiagnostics.Any(diagnostic => diagnostic.Id == "NLT002" &&
                diagnostic.GetMessage().Contains("[LiftEvent]", StringComparison.Ordinal)),
            "The v4 host module did not retain its explicit [LiftEvent] diagnostic.");
        var unsupportedStreamSource = V4HostModuleSource.Replace(
            "IAsyncEnumerable<OrderUpdate> WatchAsync(",
            "IAsyncEnumerable<int> WatchAsync(",
            StringComparison.Ordinal);
        var (_, _, unsupportedStreamDiagnostics) = Generate(
            unsupportedStreamSource,
            "NetLiftV4UnsupportedStreamItemFixture");
        Require(unsupportedStreamDiagnostics.Any(diagnostic =>
                diagnostic.Id == "NLT002" &&
                diagnostic.GetMessage().Contains("non-null [LiftDto] item type", StringComparison.Ordinal)),
            "An unsupported v4 host stream item was omitted without a diagnostic.");

        using var assemblyStream = new MemoryStream();
        var emit = compilation.Emit(assemblyStream);
        Require(emit.Success, "V4 host module assembly failed to emit: " +
            string.Join(Environment.NewLine, emit.Diagnostics
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
        assemblyStream.Position = 0;
        var assembly = AssemblyLoadContext.Default.LoadFromStream(assemblyStream);
        var bootstrap = assembly.GetType("NetLift.Generated.AndroidContractBootstrap", throwOnError: true)!;
        var describe = (DescribeV1Delegate)bootstrap.GetMethod("DescribeV1")!
            .CreateDelegate(typeof(DescribeV1Delegate));
        var register = (Func<uint>)bootstrap.GetMethod("RegisterV4")!
            .CreateDelegate(typeof(Func<uint>));
        const uint requiredFeatures = (uint)(LiftHostFeatureBits.Sync |
            LiftHostFeatureBits.AsyncCallbacks | LiftHostFeatureBits.TypedNativeReply | LiftHostFeatureBits.Streams);
        ValidateDescriptor(describe, schemaHash, requiredFeatures);

        var schemaHashBytes = Convert.FromHexString(schemaHash);
        var rejectedRegistry = new FakeDispatchRegistry(schemaHashBytes, requiredFeatures & ~(uint)LiftHostFeatureBits.Streams);
        var probe = assembly.GetType("HostFixtureV4.Probe", throwOnError: true)!;
        probe.GetMethod("Reset")!.Invoke(null, null);
        Require(RegisterWith(register, new LegacyOnlyDispatchRegistry()) ==
                    (uint)LiftBootstrapStatus.FeatureMismatch &&
                GetProbe<int>(probe, "FactoryObjectCalls") == 0,
            "RegisterV4 accepted a legacy unary-only registry or created factories before rejecting it.");
        probe.GetMethod("Reset")!.Invoke(null, null);
        Require(RegisterWith(register, rejectedRegistry) == (uint)LiftBootstrapStatus.FeatureMismatch &&
                GetProbe<int>(probe, "FactoryObjectCalls") == 0 &&
                rejectedRegistry.RouteCount == 0 && rejectedRegistry.SubscriptionCount == 0,
            "RegisterV4 created factories or published either table before required stream-feature validation.");

        probe.GetMethod("Reset")!.Invoke(null, null);
        var failedBatch = new FakeDispatchRegistry(schemaHashBytes, requiredFeatures)
        {
            V4BatchStatus = LiftBootstrapStatus.RegistrationConflict
        };
        Require(RegisterWith(register, failedBatch) == (uint)LiftBootstrapStatus.RegistrationConflict &&
                failedBatch.RouteCount == 0 && failedBatch.SubscriptionCount == 0,
            "A failed v4 batch partially published the unary or stream table.");

        probe.GetMethod("Reset")!.Invoke(null, null);
        var registry = new FakeDispatchRegistry(schemaHashBytes, requiredFeatures);
        Require(RegisterWith(register, registry) == (uint)LiftBootstrapStatus.Success &&
                registry.RouteCount == 2 && registry.SubscriptionCount == 1 &&
                registry.Contains(70, 1) && registry.Contains(69, 1) &&
                registry.ContainsSubscription(70, 2) &&
                GetProbe<int>(probe, "FactoryObjectCalls") == 1 &&
                GetProbe<int>(probe, "OrdersFactoryCalls") == 1 &&
                GetProbe<int>(probe, "TransformFactoryCalls") == 1,
            "RegisterV4 did not publish a single app-scoped service across separate unary and stream tables.");

        var firstInvoker = new RecordingNativeInvoker();
        var firstContext = new LiftInvocationContext(CancellationToken.None, 101, firstInvoker);
        var secondInvoker = new RecordingNativeInvoker();
        var secondContext = new LiftInvocationContext(CancellationToken.None, 202, secondInvoker);
        var firstCall = Task.Run(() => Invoke(registry, 70, 1, EncodeIntDto(5), firstContext));
        var secondCall = Task.Run(() => Invoke(registry, 70, 1, EncodeIntDto(6), secondContext));
        Task.WaitAll(firstCall, secondCall);
        var firstResult = firstCall.Result;
        var secondResult = secondCall.Result;
        var resultValues = new[]
        {
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(firstResult.Payload.Span[6..]),
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(secondResult.Payload.Span[6..])
        }        .OrderBy(value => value).ToArray();
        Require(firstResult.Status == LiftDispatchStatus.Success &&
                secondResult.Status == LiftDispatchStatus.Success &&
                resultValues.SequenceEqual([1, 2]) &&
                firstInvoker.CorrelationToken == 101 && secondInvoker.CorrelationToken == 202 &&
                firstInvoker.CallCount == 1 && secondInvoker.CallCount == 1 &&
                GetProbe<System.Collections.Concurrent.ConcurrentQueue<ulong>>(probe, "NativeCorrelationTokens")
                    .OrderBy(value => value).SequenceEqual([101ul, 202ul]) &&
                GetProbe<object?>(probe, "LastPlaceService") is not null,
            "V4 unary dispatch did not use the app-scoped service with each call's own native invoker.");
        try
        {
            _ = LiftInvocationContextAccessor.Current;
            throw new InvalidOperationException("The v4 unary invocation context escaped its generated scope.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message == "No NetLift invocation context is active.")
        {
        }

        var streamInvoker = new RecordingNativeInvoker();
        var streamContext = new LiftInvocationContext(CancellationToken.None, 303, streamInvoker);
        var streamHandler = registry.GetSubscription(70, 2);
        var stream = streamHandler(ReadOnlyMemory<byte>.Empty, streamContext);
        Require(GetProbe<int>(probe, "WatchCalls") == 0,
            "V4 registration invoked a stream producer before activation/enumeration.");
        var encodedItems = new List<byte[]>();
        var enumerator = stream.GetAsyncEnumerator();
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                encodedItems.Add(enumerator.Current.ToArray());
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        Require(encodedItems.Count == 1 && encodedItems[0].SequenceEqual(EncodeIntDto(2)) &&
                GetProbe<ulong>(probe, "WatchCorrelationToken") == 303 &&
                ReferenceEquals(GetProbe<object?>(probe, "LastPlaceService"),
                    GetProbe<object?>(probe, "LastWatchService")) &&
                streamInvoker.CallCount == 0 &&
                GetProbe<int>(probe, "WatchCalls") == 1,
            "V4 stream dispatch did not lazily encode items from the same app-scoped service or leaked a native invoker.");
        try
        {
            _ = LiftInvocationContextAccessor.Current;
            throw new InvalidOperationException("The v4 stream invocation context escaped its generated scope.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message == "No NetLift invocation context is active.")
        {
        }

        probe.GetField("ThrowFromWatch")!.SetValue(null, true);
        var producerFailure = registry.GetSubscription(70, 2)(
            ReadOnlyMemory<byte>.Empty,
            streamContext);
        try
        {
            var failureEnumerator = producerFailure.GetAsyncEnumerator();
            try
            {
                _ = failureEnumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult();
                throw new InvalidOperationException("A managed WatchAsync exception was silently converted.");
            }
            finally
            {
                failureEnumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        catch (InvalidOperationException exception) when (
            exception.Message == "WatchAsync buffer failed.")
        {
        }
    }

    private static void ValidateDescriptor(
        DescribeV1Delegate describe,
        string expectedHash,
        uint expectedFeatures)
    {
        unsafe
        {
            var descriptorSize = sizeof(LiftContractDescriptorV1);
            Require(descriptorSize == 48, "LiftContractDescriptorV1 layout is not 48 bytes.");
            var shortBuffer = stackalloc byte[descriptorSize - 1];
            new Span<byte>(shortBuffer, descriptorSize - 1).Fill(0xA5);
            var shortStatus = describe((LiftContractDescriptorV1*)shortBuffer, (uint)(descriptorSize - 1));
            Require(shortStatus == (uint)LiftBootstrapStatus.BufferTooSmall &&
                    new ReadOnlySpan<byte>(shortBuffer, descriptorSize - 1).ToArray().All(value => value == 0xA5),
                "DescribeV1 wrote to the short output buffer before rejecting its capacity.");
            Require(describe(null, (uint)descriptorSize) == (uint)LiftBootstrapStatus.InvalidArgument,
                "DescribeV1 accepted a null descriptor pointer.");

            LiftContractDescriptorV1 descriptor = default;
            Require(describe(&descriptor, (uint)descriptorSize) == (uint)LiftBootstrapStatus.Success &&
                    descriptor.StructSize == descriptorSize &&
                    descriptor.DescriptorVersion == 1 &&
                    descriptor.AbiVersion == 1 &&
                    descriptor.RequiredFeatureBits == expectedFeatures,
                "DescribeV1 returned an invalid size/version/ABI/features descriptor.");
            byte[] hash;
            byte* schemaHash = descriptor.SchemaHash;
            hash = new ReadOnlySpan<byte>(schemaHash, 32).ToArray();
            Require(hash.SequenceEqual(Convert.FromHexString(expectedHash)),
                "DescribeV1 hash did not match the canonical schema hash.");
        }
    }

    private static void RejectBeforeFactory(
        Func<uint> register,
        Type probe,
        FakeDispatchRegistry registry,
        LiftBootstrapStatus expected)
    {
        ResetProbe(probe);
        Require(RegisterWith(register, registry) == (uint)expected &&
                GetProbe<int>(probe, "FactoryObjectCalls") == 0 &&
                registry.RouteCount == 0,
            $"RegisterV1 did not fail closed for {expected} before factory creation or route publication.");
    }

    private static uint RegisterWith(Func<uint> register, ILiftDispatchRegistry registry)
    {
        Require(LiftGeneratedHost.TrySetRegistry(registry), "Failed to install the fake registry.");
        try
        {
            return register();
        }
        finally
        {
            Require(LiftGeneratedHost.TryClearRegistry(registry), "Failed to clear the fake registry.");
        }
    }

    private static LiftDispatchResult Invoke(
        FakeDispatchRegistry registry,
        uint serviceId,
        uint methodId,
        byte[] payload,
        LiftInvocationContext context) =>
        registry.GetRoute(serviceId, methodId)(payload, context).AsTask().GetAwaiter().GetResult();

    private static void VerifyNativeOutcomes()
    {
        var falseResult = LiftNativeReplyDecoder.GetBooleanResult(
            new LiftNativeReply(LiftNativeReplyKind.Result, new byte[] { 0 }, ReadOnlyMemory<byte>.Empty),
            CancellationToken.None);
        Require(!falseResult,
            "A normal false result was coerced into a failure or true.");

        try
        {
            LiftNativeReplyDecoder.GetResultPayload(
                new LiftNativeReply(LiftNativeReplyKind.Cancelled, default, default), CancellationToken.None);
            throw new InvalidOperationException("Typed native cancellation became a result.");
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            LiftNativeReplyDecoder.GetResultPayload(
                new LiftNativeReply(LiftNativeReplyKind.HandlerFailure, default, new byte[] { 102, 97, 105, 108 }),
                CancellationToken.None);
            throw new InvalidOperationException("Typed native handler failure became a result.");
        }
        catch (LiftNativeServiceException exception)
        {
            Require(exception.StatusCode == 13 && exception.Message == "fail" &&
                    exception.DiagnosticUtf8.Span.SequenceEqual(new byte[] { 102, 97, 105, 108 }),
                "Typed handler failure lost its status or diagnostic.");
        }

        foreach (var diagnostic in new[]
                 {
                     new byte[] { 0xFF },
                     new byte[LiftNativeReplyDecoder.MaxDiagnosticBytes + 1]
                 })
        {
            try
            {
                LiftNativeReplyDecoder.GetResultPayload(
                    new LiftNativeReply(LiftNativeReplyKind.HandlerFailure, default, diagnostic),
                    CancellationToken.None);
                throw new InvalidOperationException("Invalid or oversized typed failure diagnostic was accepted.");
            }
            catch (LiftNativeServiceException exception)
            {
                Require(exception.StatusCode == 7,
                    "Invalid or oversized diagnostic did not remain an explicit protocol error.");
            }
        }

        try
        {
            LiftNativeReplyDecoder.GetResultPayload(
                new LiftNativeReply(LiftNativeReplyKind.UnknownMethod, default, default), CancellationToken.None);
            throw new InvalidOperationException("Unknown native method became a result.");
        }
        catch (LiftNativeServiceException exception)
        {
            Require(exception.StatusCode == 3, "Unknown native method lost its explicit failure status.");
        }

        try
        {
            LiftNativeReplyDecoder.GetBooleanResult(
                new LiftNativeReply(LiftNativeReplyKind.Result, new byte[] { 2 }, default),
                CancellationToken.None);
            throw new InvalidOperationException("Malformed boolean result became a value.");
        }
        catch (LiftNativeServiceException exception)
        {
            Require(exception.StatusCode == 7, "Malformed boolean payload lost its protocol error status.");
        }
    }

    private static (IReadOnlyDictionary<string, string> GeneratedSources, Compilation OutputCompilation,
        ImmutableArray<Diagnostic> Diagnostics) Generate(string source, string assemblyName)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(LiftServiceAttribute).Assembly.Location))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable, allowUnsafe: true));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new LiftContractGenerator().AsSourceGenerator()],
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview));
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        var generatedSources = driver.GetRunResult().Results.Single().GeneratedSources
            .ToDictionary(generated => generated.HintName,
                generated => generated.SourceText.ToString(), StringComparer.Ordinal);
        return (generatedSources, output, diagnostics);
    }

    private static void VerifyIncrementalCache()
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(HostModuleSource, parseOptions);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(LiftServiceAttribute).Assembly.Location))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "NetLiftIncrementalFixture",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable, allowUnsafe: true));
        var driverOptions = new GeneratorDriverOptions(
            IncrementalGeneratorOutputKind.None,
            trackIncrementalGeneratorSteps: true);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new LiftContractGenerator().AsSourceGenerator()],
            parseOptions: parseOptions,
            driverOptions: driverOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);

        var editedText = HostModuleSource.Replace(
            "HostFixture.Probe.FactoryObjectCalls++;",
            "HostFixture.Probe.FactoryObjectCalls += 1;",
            StringComparison.Ordinal);
        var changedTree = syntaxTree.WithChangedText(SourceText.From(editedText));
        var changedCompilation = compilation.ReplaceSyntaxTree(syntaxTree, changedTree);
        driver = driver.RunGeneratorsAndUpdateCompilation(changedCompilation, out _, out _);
        var result = driver.GetRunResult().Results.Single();
        var contractReasons = result.TrackedSteps.TryGetValue("ContractSymbols", out var contractSteps)
            ? string.Join(", ", contractSteps.SelectMany(step => step.Outputs).Select(output => output.Reason))
            : "step not tracked";
        Require(result.TrackedSteps.TryGetValue("ContractSymbols", out contractSteps) &&
                contractSteps.SelectMany(step => step.Outputs)
                    .All(output => output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged),
            "An implementation-only edit invalidated contract symbols: " + contractReasons);
        Require(result.TrackedSteps.TryGetValue("HostModuleOptIn", out var optInSteps) &&
                optInSteps.SelectMany(step => step.Outputs)
                    .All(output => output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged),
            "An implementation-only edit invalidated the host opt-in marker.");
        Require(result.TrackedSteps.TryGetValue("HostFactoryShape", out var factorySteps) &&
                factorySteps.SelectMany(step => step.Outputs)
                    .All(output => output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged),
            "An implementation-only edit invalidated the factory-shape signal.");
        Require(result.TrackedSteps.TryGetValue("ContractGenerationInput", out var generationSteps) &&
                generationSteps.SelectMany(step => step.Outputs)
                    .All(output => output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged),
            "An implementation-only edit reran the combined contract generation input.");

        var bodyEditContractSource = result.GeneratedSources
            .Single(source => source.HintName == "LiftContract.g.cs").SourceText.ToString();
        var contractEditedText = editedText.Replace("[LiftMethod(2)]", "[LiftMethod(3)]", StringComparison.Ordinal);
        var contractEditedTree = changedTree.WithChangedText(SourceText.From(contractEditedText));
        var contractEditedCompilation = changedCompilation.ReplaceSyntaxTree(changedTree, contractEditedTree);
        driver = driver.RunGeneratorsAndUpdateCompilation(contractEditedCompilation, out _, out _);
        var contractEditResult = driver.GetRunResult().Results.Single();
        Require(contractEditResult.TrackedSteps.TryGetValue("ContractSymbols", out var editedContractSteps) &&
                editedContractSteps.SelectMany(step => step.Outputs)
                    .Any(output => output.Reason is IncrementalStepRunReason.Modified or IncrementalStepRunReason.New),
            "A contract edit did not invalidate the contract symbol input.");
        var contractEditSource = contractEditResult.GeneratedSources
            .Single(source => source.HintName == "LiftContract.g.cs").SourceText.ToString();
        Require(ReadSchema(bodyEditContractSource) != ReadSchema(contractEditSource),
            "A contract edit did not change the generated canonical schema.");
    }

    private static void ResetProbe(Type probe) => probe.GetMethod("Reset")!.Invoke(null, null);
    private static void SetProbe(Type probe, string fieldName, bool value) =>
        probe.GetField(fieldName)!.SetValue(null, value);
    private static T GetProbe<T>(Type probe, string fieldName) =>
        (T)probe.GetField(fieldName)!.GetValue(null)!;

    private static byte[] EncodeIntDto(int value)
    {
        var payload = new byte[10];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(payload, 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2), 4);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(6), value);
        return payload;
    }

    private static string ReadSchema(string generatedSource)
    {
        var start = generatedSource.IndexOf("public const string CanonicalJson = ", StringComparison.Ordinal);
        Require(start >= 0, "Generated source is missing CanonicalJson.");
        start = generatedSource.IndexOf('"', start);
        var end = generatedSource.IndexOf("\";", start, StringComparison.Ordinal);
        return JsonSerializer.Deserialize<string>(generatedSource[start..(end + 1)])!;
    }

    private static string FormatErrors(IEnumerable<Diagnostic> generatorDiagnostics,
        IEnumerable<Diagnostic> compilationDiagnostics) =>
        string.Join(Environment.NewLine,
            generatorDiagnostics.Concat(compilationDiagnostics)
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private unsafe delegate uint DescribeV1Delegate(LiftContractDescriptorV1* output, uint outputCapacity);

    private sealed class FakeNativeInvoker : ILiftNativeServiceInvoker
    {
        public ValueTask<LiftNativeReply> InvokeAsync(
            uint serviceId,
            uint methodId,
            ReadOnlyMemory<byte> payload,
            ulong correlationToken,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LiftNativeReply(
                LiftNativeReplyKind.Result, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty));
    }

    private sealed class RecordingNativeInvoker : ILiftNativeServiceInvoker
    {
        public int CallCount { get; private set; }
        public ulong CorrelationToken { get; private set; }

        public async ValueTask<LiftNativeReply> InvokeAsync(
            uint serviceId,
            uint methodId,
            ReadOnlyMemory<byte> payload,
            ulong correlationToken,
            CancellationToken cancellationToken)
        {
            CallCount++;
            CorrelationToken = correlationToken;
            await Task.Delay(10, cancellationToken);
            return new LiftNativeReply(
                LiftNativeReplyKind.Result, new byte[] { 1 }, ReadOnlyMemory<byte>.Empty);
        }
    }

    private sealed class LegacyOnlyDispatchRegistry : ILiftDispatchRegistry
    {
        public LiftBootstrapStatus ValidateDescriptor(
            uint descriptorVersion,
            uint abiVersion,
            uint requiredFeatureBits,
            ReadOnlySpan<byte> schemaHash) => LiftBootstrapStatus.Success;

        public LiftBootstrapStatus TryRegisterBatch(
            uint abiVersion,
            uint requiredFeatureBits,
            ReadOnlySpan<byte> schemaHash,
            IReadOnlyList<LiftMethodRegistration> registrations) => LiftBootstrapStatus.Success;
    }

    private sealed class FakeDispatchRegistry(
        byte[] expectedHash,
        uint supportedFeatures,
        uint descriptorVersion = 1,
        uint abiVersion = 1) : ILiftV4DispatchRegistry
    {
        private Dictionary<(uint ServiceId, uint MethodId), LiftManagedMethod> _routes = [];
        private Dictionary<(uint ServiceId, uint SubscriptionId), LiftStreamSubscriptionRegistration> _subscriptions = [];

        public uint AcceptedDescriptorVersion { get; } = descriptorVersion;
        public uint AcceptedAbiVersion { get; } = abiVersion;
        public LiftBootstrapStatus BatchStatus { get; init; } = LiftBootstrapStatus.Success;
        public LiftBootstrapStatus V4BatchStatus { get; init; } = LiftBootstrapStatus.Success;
        public bool ThrowDuringValidation { get; init; }
        public bool ThrowDuringBatch { get; init; }
        public int RouteCount => _routes.Count;
        public int SubscriptionCount => _subscriptions.Count;

        public LiftBootstrapStatus ValidateDescriptor(
            uint descriptorVersion,
            uint abiVersion,
            uint requiredFeatureBits,
            ReadOnlySpan<byte> schemaHash)
        {
            if (ThrowDuringValidation)
                throw new InvalidOperationException("registry validation failed");
            if (descriptorVersion != AcceptedDescriptorVersion)
                return LiftBootstrapStatus.DescriptorVersionMismatch;
            if (abiVersion != AcceptedAbiVersion)
                return LiftBootstrapStatus.AbiMismatch;
            if (!schemaHash.SequenceEqual(expectedHash))
                return LiftBootstrapStatus.SchemaMismatch;
            if ((requiredFeatureBits & ~supportedFeatures) != 0)
                return LiftBootstrapStatus.FeatureMismatch;
            return LiftBootstrapStatus.Success;
        }

        public LiftBootstrapStatus TryRegisterBatch(
            uint abiVersion,
            uint requiredFeatureBits,
            ReadOnlySpan<byte> schemaHash,
            IReadOnlyList<LiftMethodRegistration> registrations)
        {
            if (ThrowDuringBatch)
                throw new InvalidOperationException("registry batch failed");
            var validation = ValidateDescriptor(1, abiVersion, requiredFeatureBits, schemaHash);
            if (validation != LiftBootstrapStatus.Success)
                return validation;
            if (BatchStatus != LiftBootstrapStatus.Success)
                return BatchStatus;

            var staged = new Dictionary<(uint ServiceId, uint MethodId), LiftManagedMethod>(_routes);
            foreach (var registration in registrations)
            {
                var key = (registration.ServiceId, registration.MethodId);
                if (registration.Handler is null || !staged.TryAdd(key, registration.Handler))
                    return LiftBootstrapStatus.RegistrationConflict;
            }

            _routes = staged;
            return LiftBootstrapStatus.Success;
        }

        public LiftBootstrapStatus TryRegisterV4Batch(
            uint abiVersion,
            uint requiredFeatureBits,
            ReadOnlySpan<byte> schemaHash,
            IReadOnlyList<LiftMethodRegistration> methods,
            IReadOnlyList<LiftStreamSubscriptionRegistration> subscriptions)
        {
            if (ThrowDuringBatch)
                throw new InvalidOperationException("registry v4 batch failed");
            var validation = ValidateDescriptor(1, abiVersion, requiredFeatureBits, schemaHash);
            if (validation != LiftBootstrapStatus.Success)
                return validation;
            if (V4BatchStatus != LiftBootstrapStatus.Success)
                return V4BatchStatus;

            var stagedRoutes = new Dictionary<(uint ServiceId, uint MethodId), LiftManagedMethod>(_routes);
            foreach (var registration in methods)
            {
                var key = (registration.ServiceId, registration.MethodId);
                if (registration.Handler is null || !stagedRoutes.TryAdd(key, registration.Handler))
                    return LiftBootstrapStatus.RegistrationConflict;
            }

            var stagedSubscriptions =
                new Dictionary<(uint ServiceId, uint SubscriptionId), LiftStreamSubscriptionRegistration>(_subscriptions);
            foreach (var registration in subscriptions)
            {
                var key = (registration.ServiceId, registration.SubscriptionId);
                if (registration.Handler is null || !stagedSubscriptions.TryAdd(key, registration))
                    return LiftBootstrapStatus.RegistrationConflict;
            }

            _routes = stagedRoutes;
            _subscriptions = stagedSubscriptions;
            return LiftBootstrapStatus.Success;
        }

        public bool Contains(uint serviceId, uint methodId) => _routes.ContainsKey((serviceId, methodId));

        public bool ContainsSubscription(uint serviceId, uint subscriptionId) =>
            _subscriptions.ContainsKey((serviceId, subscriptionId));

        public LiftManagedMethod GetRoute(uint serviceId, uint methodId) =>
            _routes.TryGetValue((serviceId, methodId), out var handler)
                ? handler
                : throw new KeyNotFoundException($"No route registered for {serviceId}/{methodId}.");

        public LiftManagedStream GetSubscription(uint serviceId, uint subscriptionId) =>
            _subscriptions.TryGetValue((serviceId, subscriptionId), out var registration)
                ? registration.Handler
                : throw new KeyNotFoundException($"No stream subscription registered for {serviceId}/{subscriptionId}.");
    }
}
