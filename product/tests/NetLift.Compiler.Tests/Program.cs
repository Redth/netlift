using NetLift.Compiler;
using Orders.Contracts;

var assemblyPath = typeof(IOrdersService).Assembly.Location;
var first = ContractCompiler.Compile(assemblyPath, ["Orders.Contracts.IOrdersService"]);
var second = ContractCompiler.Compile(assemblyPath, ["Orders.Contracts.IOrdersService"]);
var relocatedDirectory = Path.Combine(
    Path.GetTempPath(), "netlift-compiler-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(relocatedDirectory);
var relocatedAssemblyPath = Path.Combine(relocatedDirectory, Path.GetFileName(assemblyPath));
File.Copy(assemblyPath, relocatedAssemblyPath);
ContractManifest relocated;
try
{
    relocated = ContractCompiler.Compile(
        relocatedAssemblyPath, ["Orders.Contracts.IOrdersService"]);
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
Require(first.Services[0].Methods.Count == 5, "Expected unary, streaming, adapter, and collection methods.");
var submitOrderMethod = first.Services[0].Methods.Single(
    method => method.Name == nameof(IOrdersService.SubmitOrderAsync));
var watchOrderStatusMethod = first.Services[0].Methods.Single(
    method => method.Name == nameof(IOrdersService.WatchOrderStatusAsync));
var roundTripUuidMethod = first.Services[0].Methods.Single(
    method => method.Name == nameof(IOrdersService.RoundTripUuidAsync));
Require(submitOrderMethod.IsAsync, "Expected an async unary method.");
Require(!submitOrderMethod.IsStreaming, "Unary method was marked as streaming.");
Require(watchOrderStatusMethod.IsStreaming, "Expected a streaming method.");
Require(
    submitOrderMethod.SupportsCancellation && watchOrderStatusMethod.SupportsCancellation,
    "Expected cancellation support.");
Require(first.Types.Count == 11, "Expected ten DTOs and one enum.");
Require(
    first.Adapters.Count == 5 &&
    first.Adapters.Any(adapter =>
        adapter is
        {
            Id: "netlift.uuid",
            Version: 1,
            DotNetType: "System.Guid",
            KotlinType: "java.util.UUID",
            SwiftType: "Foundation.UUID",
            WireShape: "json-string:uuid-lowercase",
            Ownership: "value",
            RequiredCapability: "uuid-v1"
        }) &&
    first.Adapters.Any(adapter =>
        adapter is
        {
            Id: "netlift.result-error",
            Version: 1,
            DotNetType: "NetLift.Runtime.NetLiftStatus",
            KotlinType: "dev.netlift.runtime.NetLiftRemoteException",
            SwiftType: "NetLiftRemoteError",
            WireShape: "transport-envelope:status-message",
            Ownership: "value",
            RequiredCapability: "result-errors-v1"
        }) &&
    first.Adapters.Select(adapter => adapter.Id).SequenceEqual(
        ["netlift.bytes", "netlift.result-error", "netlift.timestamp", "netlift.uri", "netlift.uuid"]),
    "Expected the built-in adapter metadata.");
Require(
    roundTripUuidMethod.Parameters[0].Type.Kind == "dto" &&
    roundTripUuidMethod.Result.Kind == "dto",
    "UUID method did not retain DTO request/result boundaries.");

var projections = ProjectionEmitter.Emit(first, "dev.netlift.orders", "OrdersNetLift");
Require(projections.Count == 3, "Expected Kotlin, Swift, and managed outputs.");
foreach (var projection in projections.Values)
    Require(projection.Contains(first.SchemaHash, StringComparison.Ordinal), "Generated output omitted the schema hash.");
var kotlin = projections["NetLiftContracts.kt"];
Require(
    kotlin.Contains(
        "private object NetLiftRuntimeTransport : NetLiftTransport, NetLiftStreamTransport",
        StringComparison.Ordinal) &&
    kotlin.Contains(
        "dev.netlift.runtime.NetLiftRuntime.invoke(serviceId, memberId, payload)",
        StringComparison.Ordinal) &&
    kotlin.Contains(
        "dev.netlift.runtime.NetLiftRuntime.openStream(serviceId, memberId, payload)",
        StringComparison.Ordinal),
    "Generated Kotlin runtime transport did not delegate calls and streams to the app runtime.");
Require(
    kotlin.Contains(
        "constructor() : this(NetLiftRuntimeTransport, NetLiftRuntimeTransport)",
        StringComparison.Ordinal) &&
    kotlin.Contains("private val transport: NetLiftTransport", StringComparison.Ordinal) &&
    kotlin.Contains("private val streamTransport: NetLiftStreamTransport? = null", StringComparison.Ordinal),
    "Generated Kotlin clients must default to the runtime while preserving transport injection.");
Require(
    kotlin.Contains($"const val ServiceId: UInt = {first.Services[0].Id}u", StringComparison.Ordinal),
    "Generated Kotlin client omitted its public service ID.");
Require(
    kotlin.Contains(
        $"const val SubmitOrderAsyncId: UInt = {submitOrderMethod.Id}u",
        StringComparison.Ordinal),
    "Generated Kotlin client omitted its public member ID.");
Require(
    kotlin.Contains(
        "fun watchOrderStatus(request: OrderStatusRequest): kotlinx.coroutines.flow.Flow<OrderStatusUpdate>",
        StringComparison.Ordinal),
    "Generated Kotlin client omitted its Flow projection.");
Require(
    kotlin.Contains("val value: java.util.UUID", StringComparison.Ordinal) &&
    kotlin.Contains("java.util.UUID.fromString(json.getString(\"Value\"))", StringComparison.Ordinal) &&
    kotlin.Contains("value.value.toString()", StringComparison.Ordinal),
    "Generated Kotlin omitted the UUID adapter.");
Require(
    kotlin.Contains("val timestamp: java.time.OffsetDateTime", StringComparison.Ordinal) &&
    kotlin.Contains("val payload: ByteArray", StringComparison.Ordinal) &&
    kotlin.Contains("val resource: java.net.URI", StringComparison.Ordinal) &&
    kotlin.Contains("netLiftDecodeBase64", StringComparison.Ordinal) &&
    kotlin.Contains("Invalid base64 adapter value.", StringComparison.Ordinal),
    "Generated Kotlin omitted value adapters.");
Require(
    kotlin.Contains("val values: List<Int>", StringComparison.Ordinal) &&
    kotlin.Contains("val priorities: List<OrderPriority>", StringComparison.Ordinal) &&
    kotlin.Contains("org.json.JSONArray", StringComparison.Ordinal),
    "Generated Kotlin omitted bounded collections.");
var swift = projections["NetLiftContracts.swift"];
Require(
    swift.Contains("public enum NetLiftClientRuntimeError: Error", StringComparison.Ordinal) &&
    swift.Contains("public static func install(", StringComparison.Ordinal) &&
    swift.Contains("transportAlreadyInstalled", StringComparison.Ordinal) &&
    swift.Contains("transportNotInstalled", StringComparison.Ordinal),
    "Generated Swift clients omitted one-time runtime transport installation and diagnostics.");
Require(
    swift.Contains("public init() throws", StringComparison.Ordinal) &&
    swift.Contains("NetLiftClientRuntime.resolve()", StringComparison.Ordinal) &&
    swift.Contains("transport: any NetLiftTransport,", StringComparison.Ordinal) &&
    swift.Contains("streamTransport: (any NetLiftStreamTransport)? = nil", StringComparison.Ordinal),
    "Generated Swift clients must default to the installed runtime while preserving injection.");
Require(
    swift.Contains(
        "func watchOrderStatus(request: OrderStatusRequest) -> NetLiftAsyncSequence<OrderStatusRequest, OrderStatusUpdate>",
        StringComparison.Ordinal),
    "Generated Swift client omitted its AsyncSequence projection.");
Require(
    swift.Contains("func readNext() async throws -> Data?", StringComparison.Ordinal) &&
    swift.Contains("func openStream(", StringComparison.Ordinal) &&
    swift.Contains("try await session.readNext()", StringComparison.Ordinal) &&
    swift.Contains("session.revoke()", StringComparison.Ordinal),
    "Generated Swift stream transport did not expose pull-based demand and cancellation.");
Require(
    swift.Contains($"public static let serviceId: UInt32 = {first.Services[0].Id}", StringComparison.Ordinal) &&
    swift.Contains($"public static let submitOrderId: UInt32 = {submitOrderMethod.Id}", StringComparison.Ordinal),
    "Generated Swift client omitted its public route IDs.");
Require(
    swift.Contains("public struct NetLiftRemoteError: Error, Sendable, Equatable", StringComparison.Ordinal),
    "Generated Swift omitted the typed result error.");
Require(
    swift.Contains("public let value: UUID", StringComparison.Ordinal) &&
    swift.Contains("UUID(uuidString:", StringComparison.Ordinal) &&
    swift.Contains("value.uuidString.lowercased()", StringComparison.Ordinal),
    "Generated Swift omitted canonical UUID coding.");
Require(
    swift.Contains("public let timestamp: Date", StringComparison.Ordinal) &&
    swift.Contains("public let payload: Data", StringComparison.Ordinal) &&
    swift.Contains("public let resource: URL", StringComparison.Ordinal) &&
    swift.Contains("NetLiftAdapterCodec.decodeTimestamp", StringComparison.Ordinal),
    "Generated Swift omitted value adapters.");
Require(
    swift.Contains("public let values: [Int32]", StringComparison.Ordinal) &&
    swift.Contains("public let priorities: [OrderPriority]", StringComparison.Ordinal),
    "Generated Swift omitted bounded collections.");
var managed = projections["NetLiftDispatcher.g.cs"];
Require(
    managed.Contains("DispatchStreamBytesAsync(", StringComparison.Ordinal) &&
    managed.Contains("INetLiftStreamDispatcher", StringComparison.Ordinal),
    "Generated managed output omitted its stream dispatcher.");
Require(
    managed.Contains(
        "JsonSourceGenerationOptions(UseStringEnumConverter = true)",
        StringComparison.Ordinal) &&
    managed.Contains(
        "JsonSerializable(typeof(global::Orders.Contracts.OrderRequest))",
        StringComparison.Ordinal) &&
    managed.Contains(
        "JsonSerializer.SerializeToUtf8Bytes(value, GetJsonTypeInfo<T>())",
        StringComparison.Ordinal) &&
    !managed.Contains("result.GetType()", StringComparison.Ordinal) &&
    !managed.Contains("new global::System.Text.Json.Serialization.JsonStringEnumConverter()", StringComparison.Ordinal),
    "Generated managed JSON serialization is not NativeAOT-compatible.");
var androidCompilation = ContractCompiler.CompileWithImplementations(
    assemblyPath, ["Orders.Contracts.IOrdersService"]);
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
    androidBridge.Contains(
        "Invoke(long operationToken, int serviceId, int memberId",
        StringComparison.Ordinal),
    "Generated Android bridge did not accept the native operation token.");
Require(
    androidBridge.Contains(
        "public int Cancel(long operationToken)",
        StringComparison.Ordinal),
    "Generated Android bridge omitted unary cancellation.");
Require(
    androidBridge.Contains(
        "new(\"n_Cancel\", \"(J)I\", CancelCallbackInstance)",
        StringComparison.Ordinal),
    "Generated Android bridge omitted JNI cancellation registration.");
Require(
    androidBridge.Contains(
        "NetLiftAndroidViewAdapter.Borrow(view)",
        StringComparison.Ordinal) &&
    androidBridge.Contains(
        "NetLiftAndroidViewAdapter.Retain(view)",
        StringComparison.Ordinal) &&
    androidBridge.Contains(
        "RetainedViews.TryRemove",
        StringComparison.Ordinal),
    "Generated Android bridge omitted View ownership paths.");
var androidPlatformFacade = ProjectionEmitter.EmitAndroidPlatformFacade(
    androidCompilation.Manifest,
    "dev.netlift.orders");
Require(
    androidPlatformFacade.Contains(
        "class NetLiftAndroidViewClient",
        StringComparison.Ordinal) &&
    androidPlatformFacade.Contains(
        "Looper.myLooper() == Looper.getMainLooper()",
        StringComparison.Ordinal) &&
    androidPlatformFacade.Contains(
        "fun roundTripBorrowed(view: View): View",
        StringComparison.Ordinal),
    "Generated Android View facade omitted typing or main-thread isolation.");
var appleBridge = ProjectionEmitter.EmitAppleBridge(
    androidCompilation.Manifest,
    androidCompilation.Implementations);
Require(
    appleBridge.Contains("new global::Orders.Contracts.OrdersService()", StringComparison.Ordinal),
    "Generated Apple bridge omitted deterministic service construction.");
Require(
    appleBridge.Contains(
        "NetLiftAppleViewAdapter.Borrow(view)",
        StringComparison.Ordinal) &&
    appleBridge.Contains(
        "NetLiftAppleViewAdapter.Retain(view)",
        StringComparison.Ordinal) &&
    appleBridge.Contains(
        "RetainedViews.TryRemove",
        StringComparison.Ordinal),
    "Generated Apple bridge omitted UIView ownership paths.");
var applePlatformFacade = ProjectionEmitter.EmitApplePlatformFacade(
    androidCompilation.Manifest);
Require(
    applePlatformFacade.Contains(
        "@MainActor",
        StringComparison.Ordinal) &&
    applePlatformFacade.Contains(
        "public func roundTripBorrowed(_ view: UIView) -> UIView",
        StringComparison.Ordinal) &&
    applePlatformFacade.Contains(
        "public func retain(_ view: UIView) -> Int64",
        StringComparison.Ordinal),
    "Generated Apple UIView facade omitted typing or main-actor isolation.");
Require(
    appleBridge.Contains("[global::Foundation.Register(\"NetLiftManagedBridge\")]", StringComparison.Ordinal),
    "Generated Apple bridge omitted its Objective-C registration.");
Require(
    appleBridge.Contains(
        "Invoke(long operationToken, uint serviceId, uint memberId",
        StringComparison.Ordinal),
    "Generated Apple bridge did not accept the native operation token.");
Require(
    appleBridge.Contains(
        "public int Cancel(long operationToken)",
        StringComparison.Ordinal),
    "Generated Apple bridge omitted unary cancellation.");
Require(
    appleBridge.Contains("netlift_native_ui_main", StringComparison.Ordinal),
    "Generated Apple bridge omitted the native-owned UI handoff.");

var bidirectionalCompilation = ContractCompiler.CompileWithImplementations(
    assemblyPath,
    ["Orders.Contracts.IOrdersService"],
    ["Orders.Contracts.IOrderConfirmationService"]);
Require(
    bidirectionalCompilation.Manifest.Services.Count == 2 &&
    bidirectionalCompilation.Manifest.Services.Single(
        service => service.Name == "Orders.Contracts.IOrderConfirmationService").Owner == "native" &&
    bidirectionalCompilation.Manifest.Services.Single(
        service => service.Name == "Orders.Contracts.IOrdersService").Owner == "managed",
    "Bidirectional compilation did not preserve service ownership.");
Require(
    bidirectionalCompilation.Implementations is
    [
        {
            Service: "Orders.Contracts.IOrdersService",
            Implementation: "Orders.Contracts.OrdersService",
            NativeServiceDependencies: ["Orders.Contracts.IOrderConfirmationService"]
        }
    ],
    "Native-owned services incorrectly required managed implementations.");

var bidirectionalProjections = ProjectionEmitter.Emit(
    bidirectionalCompilation.Manifest,
    "dev.netlift.orders",
    "OrdersNetLift");
var bidirectionalKotlin = bidirectionalProjections["NetLiftContracts.kt"];
Require(
    bidirectionalKotlin.Contains("interface OrderConfirmationService", StringComparison.Ordinal),
    "Generated Kotlin omitted the native-owned service interface.");
Require(
    !bidirectionalKotlin.Contains("class OrderConfirmationServiceClient", StringComparison.Ordinal),
    "Generated Kotlin incorrectly emitted a client for a native-owned service.");
Require(
    bidirectionalKotlin.Contains("fun registerOrderConfirmationService(", StringComparison.Ordinal),
    "Generated Kotlin omitted native-service registration.");
var bidirectionalSwift = bidirectionalProjections["NetLiftContracts.swift"];
Require(
    bidirectionalSwift.Contains("public protocol OrderConfirmationService", StringComparison.Ordinal),
    "Generated Swift omitted the native-owned service protocol.");
Require(
    bidirectionalSwift.Contains("public enum OrderConfirmationServiceRoutes", StringComparison.Ordinal),
    "Generated Swift omitted native-service route constants.");
Require(
    bidirectionalSwift.Contains(
        "public func registerOrderConfirmationService<Registration>(",
        StringComparison.Ordinal) &&
    bidirectionalSwift.Contains(
        "using register: (UInt32, @escaping NetLiftGeneratedNativeServiceHandler) throws -> Registration",
        StringComparison.Ordinal) &&
    bidirectionalSwift.Contains(
        "let response = try await service.confirmOrder(request: request)",
        StringComparison.Ordinal),
    "Generated Swift omitted typed native-service registration.");
Require(
    !bidirectionalSwift.Contains("public struct OrderConfirmationServiceClient", StringComparison.Ordinal),
    "Generated Swift incorrectly emitted a client for a native-owned service.");
var bidirectionalManaged = bidirectionalProjections["NetLiftDispatcher.g.cs"];
Require(
    bidirectionalManaged.Contains(
        "public sealed class OrderConfirmationServiceNativeProxy",
        StringComparison.Ordinal),
    "Generated managed output omitted the typed native-service proxy.");
var nativeService = bidirectionalCompilation.Manifest.Services.Single(service => service.Owner == "native");
Require(
    !bidirectionalManaged.Contains($"case {nativeService.Id}u:", StringComparison.Ordinal),
    "Generated managed dispatcher accepted a native-owned service route.");

var bidirectionalAndroidBridge = ProjectionEmitter.EmitAndroidBridge(
    bidirectionalCompilation.Manifest,
    bidirectionalCompilation.Implementations);
Require(
    bidirectionalAndroidBridge.Contains(
        "new global::Orders.Contracts.OrdersService(new global::NetLift.Generated.OrderConfirmationServiceNativeProxy(AndroidNativeServiceTransport.Instance))",
        StringComparison.Ordinal),
    "Bidirectional Android bridge omitted its managed service.");
Require(
    !bidirectionalAndroidBridge.Contains(
        "new global::Orders.Contracts.IOrderConfirmationService",
        StringComparison.Ordinal),
    "Bidirectional Android bridge tried to construct a native-owned service.");
Require(
    bidirectionalAndroidBridge.Contains(
        "netlift_android_native_service_invoke",
        StringComparison.Ordinal),
    "Bidirectional Android bridge omitted the native-service transport entry point.");
var bidirectionalAppleBridge = ProjectionEmitter.EmitAppleBridge(
    bidirectionalCompilation.Manifest,
    bidirectionalCompilation.Implementations);
Require(
    bidirectionalAppleBridge.Contains(
        "new global::Orders.Contracts.OrdersService(new global::NetLift.Generated.OrderConfirmationServiceNativeProxy(AppleNativeServiceTransport.Instance))",
        StringComparison.Ordinal),
    "Bidirectional Apple bridge omitted its managed service.");
Require(
    bidirectionalAppleBridge.Contains(
        "netlift_apple_native_service_invoke",
        StringComparison.Ordinal),
    "Bidirectional Apple bridge omitted the native-service transport entry point.");

try
{
    ContractCompiler.Compile(
        assemblyPath,
        ["Orders.Contracts.IOrdersService"],
        ["Orders.Contracts.IOrdersService"]);
    throw new InvalidOperationException("Overlapping service ownership did not fail.");
}
catch (ContractCompilerException exception)
{
    Require(
        exception.Message.Contains("both managed-owned and native-owned", StringComparison.Ordinal),
        "Overlapping service ownership produced an unclear diagnostic.");
}

var managedConfirmationLock = ContractCompiler.CreateLock(
    ContractCompiler.Compile(assemblyPath, ["Orders.Contracts.IOrderConfirmationService"]));
var nativeConfirmationLock = ContractCompiler.CreateLock(
    ContractCompiler.Compile(
        assemblyPath,
        [],
        ["Orders.Contracts.IOrderConfirmationService"]));
var nativeOnlyManifest = ContractCompiler.Compile(
    assemblyPath,
    [],
    ["Orders.Contracts.IOrderConfirmationService"]);
Require(
    ProjectionEmitter.EmitAndroidBridge(nativeOnlyManifest, [])
        .Contains("new global::NetLift.Generated.ContractDispatcherAdapter()", StringComparison.Ordinal),
    "Native-only Android bridge did not emit a valid empty managed dispatcher.");
Require(
    ProjectionEmitter.EmitAppleBridge(nativeOnlyManifest, [])
        .Contains("new global::NetLift.Generated.ContractDispatcherAdapter()", StringComparison.Ordinal),
    "Native-only Apple bridge did not emit a valid empty managed dispatcher.");
Require(
    ContractCompiler.Compare(managedConfirmationLock, nativeConfirmationLock)
        .Any(error => error.Contains("changed", StringComparison.Ordinal)),
    "Changing service ownership was not compatibility-significant.");

var contractLock = ContractCompiler.CreateLock(first);
Require(ContractCompiler.Compare(contractLock, contractLock).Count == 0, "Identical locks were incompatible.");
var preUuidLock = contractLock with
{
    Entries = contractLock.Entries
        .Where(entry =>
            entry.Kind != "adapter" &&
            !entry.Name.Contains("Uuid", StringComparison.Ordinal))
        .ToArray()
};
Require(
    ContractCompiler.Compare(preUuidLock, contractLock).Count == 0,
    "An additive UUID method and adapter were not backward compatible.");
var changedAdapterLock = contractLock with
{
    Entries = contractLock.Entries
        .Select(entry => entry.Kind == "adapter" && entry.Name == "netlift.result-error"
            ? entry with { Signature = entry.Signature.Replace(":1:", ":2:", StringComparison.Ordinal) }
            : entry)
        .ToArray()
};
Require(
    ContractCompiler.Compare(contractLock, changedAdapterLock)
        .Any(error => error.Contains("adapter", StringComparison.Ordinal) &&
            error.Contains("changed signature", StringComparison.Ordinal)),
    "An adapter version change was not compatibility-significant.");
var changedAdapterOwnershipLock = contractLock with
{
    Entries = contractLock.Entries
        .Select(entry => entry.Kind == "adapter" && entry.Name == "netlift.result-error"
            ? entry with
            {
                Signature = entry.Signature.Replace(
                    ":value:result-errors-v1",
                    ":borrowed:result-errors-v1",
                    StringComparison.Ordinal)
            }
            : entry)
        .ToArray()
};
Require(
    ContractCompiler.Compare(contractLock, changedAdapterOwnershipLock)
        .Any(error => error.Contains("netlift.result-error", StringComparison.Ordinal) &&
            error.Contains("changed signature", StringComparison.Ordinal)),
    "A result-error ownership change was not compatibility-significant.");
var noteField = contractLock.Entries.Single(entry =>
    entry.Kind == "field" &&
    entry.Name == "Orders.Contracts.OrderRequest.Note");
var beforeOptionalNoteLock = contractLock with
{
    Entries = contractLock.Entries
        .Where(entry => entry != noteField)
        .ToArray()
};
Require(
    ContractCompiler.Compare(beforeOptionalNoteLock, contractLock).Count == 0,
    "A bilaterally negotiated optional field addition was incompatible.");
var unnegotiatedOptionalLock = beforeOptionalNoteLock with { Capabilities = [] };
Require(
    ContractCompiler.Compare(unnegotiatedOptionalLock, contractLock)
        .Any(error => error.Contains("bilateral optional-fields-v1", StringComparison.Ordinal)),
    "An unnegotiated optional field addition was accepted.");
Require(
    ContractCompiler.Compare(contractLock, contractLock with { Capabilities = [] })
        .Any(error => error.Contains("required capability", StringComparison.Ordinal)),
    "A required capability removal was accepted.");
var requiredFieldLock = contractLock with
{
    Entries = contractLock.Entries
        .Append(new ContractLockEntry(
            "field",
            1,
            "Orders.Contracts.OrderRequest.RequiredAddition",
            "Orders.Contracts.OrderRequest.RequiredAddition:string:-:!"))
        .ToArray()
};
Require(
    ContractCompiler.Compare(contractLock, requiredFieldLock)
        .Any(error => error.Contains("required field", StringComparison.Ordinal)),
    "A required field addition was accepted.");
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
    Require(exception.Message.Contains("must contain a non-null scalar or enum", StringComparison.Ordinal),
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
