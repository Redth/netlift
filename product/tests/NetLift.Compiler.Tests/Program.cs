using System.Text.Json;
using NetLift.Compiler;
using NetLift.Compiler.Tests.Fixtures;
using Orders.Contracts;

var assemblyPath = typeof(IOrdersService).Assembly.Location;
var contractRoots = new[] { "Orders.Contracts.IOrdersService" };
var nativeServiceRoots = new[] { "Orders.Contracts.IOrderConfirmation" };
var first = ContractCompiler.Compile(assemblyPath, contractRoots, nativeServiceRoots);
var second = ContractCompiler.Compile(assemblyPath, contractRoots, nativeServiceRoots);
var relocatedDirectory = Path.Combine(
    Path.GetTempPath(), "netlift-compiler-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(relocatedDirectory);
var relocatedAssemblyPath = Path.Combine(relocatedDirectory, Path.GetFileName(assemblyPath));
File.Copy(assemblyPath, relocatedAssemblyPath);
ContractManifest relocated;
try
{
    relocated = ContractCompiler.Compile(
        relocatedAssemblyPath, contractRoots, nativeServiceRoots);
}
finally
{
    Directory.Delete(relocatedDirectory, recursive: true);
}

Require(first.SchemaHash == second.SchemaHash, "Repeated compilation changed the schema hash.");
Require(
    ContractCompiler.Serialize(first) == ContractCompiler.Serialize(second),
    "Repeated compilation was not byte deterministic.");
Require(
    ContractCompiler.Serialize(first) == ContractCompiler.Serialize(relocated),
    "Absolute assembly location changed the canonical contract.");
Require(first.Services.Count == 1, "Expected one service.");
Require(first.Services[0].Methods.Count == 3, "Expected unary, callback, and streaming methods.");
var unaryMethod = first.Services[0].Methods.Single(method => method.Name == "SubmitOrderAsync");
var streamMethod = first.Services[0].Methods.Single(method => method.IsStreaming);
Require(unaryMethod.IsAsync, "Expected the unary method to be async.");
Require(unaryMethod.SupportsCancellation, "Expected unary cancellation support.");
Require(streamMethod.SupportsCancellation, "Expected stream cancellation support.");
Require(first.Types.Count == 6, "Expected five DTOs and one enum.");

var withNativeService = ContractCompiler.Compile(
    typeof(IConfirmationHandler).Assembly.Location,
    ["NetLift.Compiler.Tests.Fixtures.IManagedServiceFixture"],
    ["NetLift.Compiler.Tests.Fixtures.IConfirmationHandler"]);
Require(
    withNativeService.NativeServices is
        [{ Name: "NetLift.Compiler.Tests.Fixtures.IConfirmationHandler" }],
    "Compiler did not preserve the explicitly configured native service root.");
var nativeServiceProjections = ProjectionEmitter.Emit(
    withNativeService,
    "dev.netlift.orders",
    "OrdersNetLift");
Require(
    nativeServiceProjections["NetLiftContracts.kt"].Contains(
        "interface ConfirmationHandler", StringComparison.Ordinal) &&
    nativeServiceProjections["NetLiftContracts.kt"].Contains(
        "suspend fun confirm(request: ConfirmationRequest): ConfirmationResult",
        StringComparison.Ordinal),
    "Generated Kotlin projection omitted the native service interface.");
Require(
    nativeServiceProjections["NetLiftContracts.kt"].Contains(
        "NetLiftNativeServiceDispatcher()", StringComparison.Ordinal) &&
    nativeServiceProjections["NetLiftContracts.kt"].Contains(
        "confirmationHandler: ConfirmationHandler", StringComparison.Ordinal) &&
    nativeServiceProjections["NetLiftContracts.kt"].Contains(
        ".put(\"nativeServices\", callbackPayload)", StringComparison.Ordinal),
    "Generated Kotlin projection omitted native callback registration and request encoding.");
Require(
    nativeServiceProjections["NetLiftContracts.swift"].Contains(
        "public protocol ConfirmationHandler: Sendable", StringComparison.Ordinal) &&
    nativeServiceProjections["NetLiftContracts.swift"].Contains(
        "func confirm(request: ConfirmationRequest) async throws -> ConfirmationResult",
        StringComparison.Ordinal),
    "Generated Swift projection omitted the native service protocol.");
Require(
    nativeServiceProjections["NetLiftContracts.swift"].Contains(
        "NetLiftNativeServiceDispatcher", StringComparison.Ordinal) &&
    nativeServiceProjections["NetLiftContracts.swift"].Contains(
        "withNativeServices", StringComparison.Ordinal) &&
    nativeServiceProjections["NetLiftContracts.swift"].Contains(
        "\"nativeServices\": nativeServices", StringComparison.Ordinal),
    "Generated Swift projection omitted native callback registration and request encoding.");
var nativeCompilation = ContractCompiler.CompileWithImplementations(
    typeof(IConfirmationHandler).Assembly.Location,
    ["NetLift.Compiler.Tests.Fixtures.IManagedServiceFixture"],
    ["NetLift.Compiler.Tests.Fixtures.IConfirmationHandler"]);
Require(
    nativeCompilation.Implementations is
        [{
            Service: "NetLift.Compiler.Tests.Fixtures.IManagedServiceFixture",
            Implementation: "NetLift.Compiler.Tests.Fixtures.ManagedServiceFixture"
        }],
    "Compiler did not discover the managed callback service implementation.");
var nativeDispatcher = ProjectionEmitter.Emit(
    nativeCompilation.Manifest,
    "dev.netlift.test",
    "NetLiftTest")["NetLiftDispatcher.g.cs"];
Require(
    nativeDispatcher.Contains("NetLiftNativeService_ConfirmationHandlerProxy", StringComparison.Ordinal) &&
    nativeDispatcher.Contains("InvokeNativeServiceAsync", StringComparison.Ordinal) &&
    nativeDispatcher.Contains("DecodeNativeServiceRequest_", StringComparison.Ordinal) &&
    nativeDispatcher.Contains("payloadRoot.GetProperty(\"nativeServices\")", StringComparison.Ordinal) &&
    nativeDispatcher.Contains("new global::NetLift.Runtime.NetLiftCallbackRegistration", StringComparison.Ordinal),
    "Generated managed dispatcher omitted native-service proxy decoding or invocation.");
var nativeAndroidBridge = ProjectionEmitter.EmitAndroidBridge(
    nativeCompilation.Manifest,
    nativeCompilation.Implementations);
Require(
    nativeAndroidBridge.Contains(
        "new global::NetLift.Compiler.Tests.Fixtures.ManagedServiceFixture()",
        StringComparison.Ordinal),
    "Generated Android bridge omitted its managed service implementation.");
Require(
    nativeAndroidBridge.Contains(
        "new global::NetLift.Generated.ContractDispatcherAdapter(NativeCallbacks,",
        StringComparison.Ordinal),
    "Generated Android bridge did not share its callback registry with the dispatcher.");
var nativeAppleBridge = ProjectionEmitter.EmitAppleBridge(
    nativeCompilation.Manifest,
    nativeCompilation.Implementations);
Require(
    nativeAndroidBridge.Contains("RegisterNativeService", StringComparison.Ordinal) &&
    nativeAndroidBridge.Contains("n_RegisterNativeService", StringComparison.Ordinal) &&
    nativeAndroidBridge.Contains("n_RevokeNativeService", StringComparison.Ordinal) &&
    nativeAndroidBridge.Contains("RegisterNativeServiceFromJava", StringComparison.Ordinal) &&
    nativeAndroidBridge.Contains("netlift_android_invoke_native_service", StringComparison.Ordinal) &&
    nativeAppleBridge.Contains(
        "registerNativeService:ownerToken:context:invoke:cancel:release:",
        StringComparison.Ordinal) &&
    nativeAppleBridge.Contains("InvokeNativeServiceAsync", StringComparison.Ordinal),
    "Generated platform bridges omitted callback registration, invocation, or draining.");
var multiCallbackCompilation = ContractCompiler.CompileWithImplementations(
    typeof(IConfirmationHandler).Assembly.Location,
    ["NetLift.Compiler.Tests.Fixtures.IMultiCallbackServiceFixture"],
    [
        "NetLift.Compiler.Tests.Fixtures.IConfirmationHandler",
        "NetLift.Compiler.Tests.Fixtures.IAuditHandler",
    ]);
var multiCallbackKotlin = ProjectionEmitter.Emit(
    multiCallbackCompilation.Manifest,
    "dev.netlift.test",
    "NetLiftTest")["NetLiftContracts.kt"];
var multiCallbackSwift = ProjectionEmitter.Emit(
    multiCallbackCompilation.Manifest,
    "dev.netlift.test",
    "NetLiftTest")["NetLiftContracts.swift"];
var multiCallbackDispatcher = ProjectionEmitter.Emit(
    multiCallbackCompilation.Manifest,
    "dev.netlift.test",
    "NetLiftTest")["NetLiftDispatcher.g.cs"];
Require(
    multiCallbackKotlin.Contains("confirmationHandlerLease", StringComparison.Ordinal) &&
    multiCallbackKotlin.Contains("auditHandlerLease", StringComparison.Ordinal) &&
    multiCallbackKotlin.Contains(
        "ConfirmationHandlerDispatcher(confirmationHandler)",
        StringComparison.Ordinal) &&
    multiCallbackKotlin.Contains(
        "AuditHandlerDispatcher(auditHandler)",
        StringComparison.Ordinal),
    "Generated Kotlin projection omitted one of multiple native callback registrations.");
Require(
    multiCallbackSwift.Contains("(\"confirmationHandler\",", StringComparison.Ordinal) &&
    multiCallbackSwift.Contains("(\"auditHandler\",", StringComparison.Ordinal),
    "Generated Swift projection omitted one of multiple native callback registrations.");
Require(
    multiCallbackDispatcher.Contains(
        "NetLiftNativeService_ConfirmationHandlerProxy",
        StringComparison.Ordinal) &&
    multiCallbackDispatcher.Contains(
        "NetLiftNativeService_AuditHandlerProxy",
        StringComparison.Ordinal),
    "Generated managed dispatcher omitted one of multiple native callback proxies.");
Require(
    ContractCompiler.CreateLock(withNativeService).Entries.Any(
        entry => entry.Kind == "nativeServiceMethod" &&
                 entry.Name == "NetLift.Compiler.Tests.Fixtures.IConfirmationHandler.ConfirmAsync"),
    "Compatibility lock omitted the native service method.");
var nativeServiceConfiguration = JsonSerializer.Deserialize(
    """
    {
      "version": 1,
      "assembly": "contracts.dll",
      "contractRoots": ["NetLift.Compiler.Tests.Fixtures.IManagedServiceFixture"],
      "kotlinPackage": "dev.netlift.test",
      "swiftModule": "NetLiftTest",
      "output": "generated",
      "nativeServiceRoots": ["NetLift.Compiler.Tests.Fixtures.IConfirmationHandler"]
    }
    """,
    ContractJsonContext.Default.CompilerConfiguration);
Require(
    nativeServiceConfiguration?.NativeServiceRoots is
        ["NetLift.Compiler.Tests.Fixtures.IConfirmationHandler"],
    "Compiler configuration did not deserialize nativeServiceRoots.");
try
{
    ContractCompiler.Compile(
        typeof(IConfirmationHandler).Assembly.Location,
        ["NetLift.Compiler.Tests.Fixtures.IManagedServiceFixture"],
        ["NetLift.Compiler.Tests.Fixtures.IManagedServiceFixture"]);
    throw new InvalidOperationException("A service declared in both ownership directions was accepted.");
}
catch (ContractCompilerException exception)
{
    Require(exception.Message.Contains("both managed-owned and native-owned", StringComparison.Ordinal),
        "Conflicting service ownership produced an unclear diagnostic.");
}
try
{
    ContractCompiler.Compile(
        typeof(IConfirmationHandler).Assembly.Location,
        ["NetLift.Compiler.Tests.Fixtures.IManagedServiceFixture"],
        [
            "NetLift.Compiler.Tests.Fixtures.IConfirmationHandler",
            "NetLift.Compiler.Tests.Fixtures.IStreamingNativeService",
        ]);
    throw new InvalidOperationException("A native service stream was accepted before support exists.");
}
catch (ContractCompilerException exception)
{
    Require(exception.Message.Contains("native service streams are not supported yet", StringComparison.Ordinal),
        $"Unsupported native service stream produced an unclear diagnostic: {exception.Message}");
}
try
{
    ContractCompiler.Compile(
        typeof(IConfirmationHandler).Assembly.Location,
        ["NetLift.Compiler.Tests.Fixtures.IStreamingManagedServiceFixture"],
        ["NetLift.Compiler.Tests.Fixtures.IConfirmationHandler"]);
    throw new InvalidOperationException("A streaming managed method with a callback was accepted.");
}
catch (ContractCompilerException exception)
{
    Require(exception.Message.Contains("cannot accept native service callbacks", StringComparison.Ordinal),
        "Unsupported streaming callback parameters produced an unclear diagnostic.");
}
try
{
    ContractCompiler.Compile(
        typeof(IConfirmationHandler).Assembly.Location,
        ["NetLift.Compiler.Tests.Fixtures.IManagedServiceFixture"],
        [
            "NetLift.Compiler.Tests.Fixtures.IConfirmationHandler",
            "NetLift.Compiler.Tests.Fixtures.IRecursiveNativeServiceFixture",
        ]);
    throw new InvalidOperationException("A native service callback parameter was accepted recursively.");
}
catch (ContractCompilerException exception)
{
    Require(exception.Message.Contains("cannot accept another native service callback", StringComparison.Ordinal),
        "Unsupported nested native service callbacks produced an unclear diagnostic.");
}

var projections = ProjectionEmitter.Emit(first, "dev.netlift.orders", "OrdersNetLift");
Require(projections.Count == 3, "Expected Kotlin, Swift, and managed outputs.");
foreach (var projection in projections.Values)
    Require(projection.Contains(first.SchemaHash, StringComparison.Ordinal), "Generated output omitted the schema hash.");
var kotlin = projections["NetLiftContracts.kt"];
Require(
    kotlin.Contains($"const val ServiceId: UInt = {first.Services[0].Id}u", StringComparison.Ordinal),
    "Generated Kotlin client omitted its public service ID.");
Require(
    kotlin.Contains(
        $"const val SubmitOrderAsyncId: UInt = {unaryMethod.Id}u",
        StringComparison.Ordinal),
    "Generated Kotlin client omitted its public member ID.");
Require(kotlin.Contains("kotlinx.coroutines.flow.Flow<OrderEvent>", StringComparison.Ordinal),
    "Generated Kotlin client omitted its stream facade.");
Require(kotlin.Contains("object NetLiftAndroidTransport : NetLiftTransport", StringComparison.Ordinal),
    "Generated Kotlin contracts omitted the Android runtime transport.");
Require(kotlin.Contains("NetLiftRuntime.stream(serviceId, memberId, payload)", StringComparison.Ordinal),
    "Generated Android transport omitted stream delegation.");
Require(
    kotlin.Contains(
        "confirmation: OrderConfirmation",
        StringComparison.Ordinal) &&
    kotlin.Contains(
        "NetLiftRuntime.registerNativeService(",
        StringComparison.Ordinal) &&
    kotlin.Contains(
        "closeNativeServices(callbackLeases)",
        StringComparison.Ordinal),
    "Generated Kotlin client omitted native callback registration or cleanup.");
Require(
    projections["NetLiftDispatcher.g.cs"].Contains("ContractJsonSerializerContext", StringComparison.Ordinal),
    "Generated managed dispatcher omitted trimming-safe JSON metadata.");
var swift = projections["NetLiftContracts.swift"];
Require(swift.Contains("AsyncThrowingStream<OrderEvent, any Error>", StringComparison.Ordinal),
    "Generated Swift client omitted its stream facade.");
Require(
    swift.Contains(
        "confirmation: any OrderConfirmation",
        StringComparison.Ordinal) &&
    swift.Contains(
        "withNativeServices([",
        StringComparison.Ordinal),
    "Generated Swift client omitted native callback registration.");
Require(
    !swift.Contains("class NetLiftAppleTransport", StringComparison.Ordinal),
    "Portable SwiftPM projection unexpectedly depends on the workload-mode Objective-C bridge.");
var appleProjections = ProjectionEmitter.Emit(
    first,
    "dev.netlift.orders",
    "OrdersNetLift",
    emitAppleTransport: true);
Require(
    appleProjections["NetLiftContracts.swift"].Contains(
        "public final class NetLiftAppleTransport: NetLiftTransport",
        StringComparison.Ordinal),
    "Apple workload projection omitted its generated runtime transport.");
Require(
    appleProjections["NetLiftContracts.swift"].Contains("openStreamOperation", StringComparison.Ordinal),
    "Generated Apple transport omitted managed stream delivery.");
var androidCompilation = ContractCompiler.CompileWithImplementations(
    assemblyPath, contractRoots, nativeServiceRoots);
Require(
    androidCompilation.Implementations is
    [
        { Service: "Orders.Contracts.IOrdersService", Implementation: "Orders.Contracts.OrdersService" }
    ],
    "Android compilation did not discover the service implementation.");
var androidBridge = ProjectionEmitter.EmitAndroidBridge(
    androidCompilation.Manifest,
    androidCompilation.Implementations);
Require(
    androidBridge.Contains("new global::Orders.Contracts.OrdersService()", StringComparison.Ordinal),
    "Generated Android bridge omitted deterministic service construction.");
Require(
    androidBridge.Contains("[global::Java.Interop.Export(\"openStream\")]", StringComparison.Ordinal) &&
    androidBridge.Contains("[global::Java.Interop.Export(\"nextStream\")]", StringComparison.Ordinal) &&
    androidBridge.Contains("[global::Java.Interop.Export(\"closeStream\")]", StringComparison.Ordinal) &&
    androidBridge.Contains("n_OpenStream", StringComparison.Ordinal) &&
    androidBridge.Contains("n_NextStream", StringComparison.Ordinal) &&
    androidBridge.Contains("n_CloseStream", StringComparison.Ordinal),
    "Generated Android bridge omitted stream lifecycle operations.");
Require(
    androidBridge.Contains("lock (state.SyncRoot)", StringComparison.Ordinal),
    "Generated Android stream cleanup is not serialized with pending reads.");
Require(
    androidBridge.Contains("AndroidBridgeJsonSerializerContext", StringComparison.Ordinal),
    "Generated Android bridge omitted trimming-safe response metadata.");
var appleBridge = ProjectionEmitter.EmitAppleBridge(
    androidCompilation.Manifest,
    androidCompilation.Implementations);
Require(
    appleBridge.Contains("new global::Orders.Contracts.OrdersService()", StringComparison.Ordinal),
    "Generated Apple bridge omitted deterministic service construction.");
Require(
    appleBridge.Contains("[global::Foundation.Register(\"NetLiftManagedBridge\")]", StringComparison.Ordinal),
    "Generated Apple bridge omitted its Objective-C registration.");
Require(
    appleBridge.Contains("netlift_native_ui_main", StringComparison.Ordinal),
    "Generated Apple bridge omitted the native-owned UI handoff.");
Require(
    appleBridge.Contains("[global::Foundation.Export(\"openStreamOperation:service:member:payload:\")]", StringComparison.Ordinal) &&
    appleBridge.Contains("[global::Foundation.Export(\"nextStreamOperation:\")]", StringComparison.Ordinal) &&
    appleBridge.Contains("[global::Foundation.Export(\"closeStreamOperation:\")]", StringComparison.Ordinal),
    "Generated Apple bridge omitted stream lifecycle operations.");
Require(
    appleBridge.Contains("AppleBridgeJsonSerializerContext", StringComparison.Ordinal),
    "Generated Apple bridge omitted trimming-safe response metadata.");

var contractLock = ContractCompiler.CreateLock(first);
Require(ContractCompiler.Compare(contractLock, contractLock).Count == 0, "Identical locks were incompatible.");
var changedLock = contractLock with
{
    Entries = contractLock.Entries
        .Where(entry => entry.Kind != "method")
        .ToArray()
};
Require(
    ContractCompiler.Compare(contractLock, changedLock).Any(error => error.Contains("removed", StringComparison.Ordinal)),
    "A removed method was not detected.");

try
{
    ContractCompiler.Compile(assemblyPath, ["Orders.Contracts.DoesNotExist"]);
    throw new InvalidOperationException("Missing root did not fail.");
}
catch (ContractCompilerException)
{
}

try
{
    ContractCompiler.Compile(assemblyPath, ["Orders.Contracts.IUnsupportedService"]);
    throw new InvalidOperationException("Unsupported collection signature did not fail.");
}
catch (ContractCompilerException exception)
{
    Require(exception.Message.Contains("not a supported scalar, enum, or DTO", StringComparison.Ordinal),
        "Unsupported signature produced an unclear diagnostic.");
}

try
{
    ContractCompiler.CompileWithImplementations(
        assemblyPath, ["Orders.Contracts.IMissingService"]);
    throw new InvalidOperationException("Missing service implementation did not fail.");
}
catch (ContractCompilerException exception)
{
    Require(
        exception.Message.Contains("requires one public", StringComparison.Ordinal),
        "Missing implementation produced an unclear diagnostic.");
}

try
{
    ContractCompiler.CompileWithImplementations(
        assemblyPath, ["Orders.Contracts.IAmbiguousService"]);
    throw new InvalidOperationException("Ambiguous service implementation did not fail.");
}
catch (ContractCompilerException exception)
{
    Require(
        exception.Message.Contains("multiple constructible implementations", StringComparison.Ordinal),
        "Ambiguous implementations produced an unclear diagnostic.");
}

Console.WriteLine($"Compiler tests passed for schema {first.SchemaHash}.");
return;

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
