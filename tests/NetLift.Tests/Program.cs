using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetLift;
using NetLift.Generator;
using NetLift.Negotiation;
using NetLift.Projection;

var source = """
    #nullable enable
    using NetLift;
    using System.Threading;
    using System.Threading.Tasks;
    [LiftDto("person", Id = 2, Version = 1)]
    public sealed record Person(
        [property: LiftField(1)] string Name,
        [property: LiftField(2)] int Age);

    [LiftDto("order-draft", Id = 3, Version = 1)]
    public sealed record OrderDraft(
        [property: LiftField(1)] string Sku,
        [property: LiftField(2)] int Quantity);

    [LiftDto("order-receipt", Id = 4, Version = 1)]
    public sealed record OrderReceipt(
        [property: LiftField(1)] long Id,
        [property: LiftField(2)] string Sku,
        [property: LiftField(3)] int Quantity);

    [LiftDto("scalar-types", Id = 5, Version = 1)]
    public sealed record ScalarTypes(
        [property: LiftField(1)] bool Flag,
        [property: LiftField(2)] byte Unsigned8,
        [property: LiftField(3)] sbyte Signed8,
        [property: LiftField(4)] short Signed16,
        [property: LiftField(5)] ushort Unsigned16,
        [property: LiftField(6)] int Signed32,
        [property: LiftField(7)] uint Unsigned32,
        [property: LiftField(8)] long Signed64,
        [property: LiftField(9)] ulong Unsigned64,
        [property: LiftField(10)] float Float32,
        [property: LiftField(11)] double Float64,
        [property: LiftField(12)] string? Optional);

    [LiftService("echo", Id = 1, Version = 1)]
    public interface IEchoService
    {
        [LiftMethod(9)]
        int EchoAge(int value);

        [LiftMethod(7)]
        Person Echo(Person input);

        [LiftMethod(11)]
        Task<OrderReceipt> PlaceAsync(OrderDraft input, CancellationToken cancellationToken);
    }

    [LiftNativeService("confirmation", Id = 3, Version = 1)]
    public interface IConfirmation
    {
        [LiftMethod(5)]
        Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken);
    }

    public sealed class EchoService : IEchoService
    {
        public int EchoAge(int value) => value;
        public Person Echo(Person input) => input with { Age = input.Age + 1 };
        public Task<OrderReceipt> PlaceAsync(OrderDraft input, CancellationToken cancellationToken) =>
            Task.FromResult(new OrderReceipt(42, input.Sku, input.Quantity));
    }
    """;

var (generated, outputCompilation, generatorDiagnostics) = Generate(source);
var (generatedAgain, _, _) = Generate(source);
Require(generated == generatedAgain, "Generator output changed across identical compilations.");
Require(Fingerprint(generated) == "8dd49b72a3862b89efbf8b23b17976a0af392e7a8bdda688a76bce247ca469f1",
    "Schema v2 generated C# changed byte-for-byte.");
Require(!generatorDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
    "Valid contract produced generator errors.");
Require(!outputCompilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
    "Generated contract did not compile: " + string.Join(Environment.NewLine,
        outputCompilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));

using var json = JsonDocument.Parse(ReadSchema(generated));
var root = json.RootElement;
Require(root.GetProperty("services")[0].GetProperty("id").GetInt32() == 1, "Service ID was not stable.");
Require(root.GetProperty("services")[0].GetProperty("methods")[0].GetProperty("id").GetInt32() == 7,
    "Method ID was not stable.");
Require(root.GetProperty("services")[0].GetProperty("methods").GetArrayLength() == 3 &&
    root.GetProperty("services")[0].GetProperty("methods")[1].GetProperty("id").GetInt32() == 9,
    "Methods were not sorted by their stable IDs.");
Require(root.GetProperty("schemaVersion").GetInt32() == 2 &&
    root.GetProperty("services").GetArrayLength() == 2 &&
    root.GetProperty("services")[0].GetProperty("kind").GetString() == "managed" &&
    root.GetProperty("services")[0].GetProperty("methods")[2].GetProperty("kind").GetString() == "async" &&
    root.GetProperty("services")[0].GetProperty("methods")[2].GetProperty("supportsCancellation").GetBoolean() &&
    root.GetProperty("services")[0].GetProperty("methods")[2].GetProperty("parameters")[0]
        .GetProperty("type").GetProperty("typeId").GetInt32() == 3 &&
    root.GetProperty("services")[0].GetProperty("methods")[2].GetProperty("returnType").GetProperty("typeId").GetInt32() == 4 &&
    root.GetProperty("services")[1].GetProperty("kind").GetString() == "native" &&
    root.GetProperty("services")[1].GetProperty("methods")[0].GetProperty("returnType").GetProperty("kind").GetString() == "bool",
    "Canonical IR omitted async/native service kinds, result types, or cancellation support.");
Require(root.GetProperty("dtos").GetArrayLength() == 4 &&
    root.GetProperty("dtos")[1].GetProperty("fields")[0].GetProperty("type").GetProperty("kind").GetString() == "string" &&
    root.GetProperty("dtos")[1].GetProperty("fields")[1].GetProperty("type").GetProperty("kind").GetString() == "int32" &&
    root.GetProperty("dtos")[2].GetProperty("fields").GetArrayLength() == 3 &&
    root.GetProperty("dtos")[2].GetProperty("fields")[0].GetProperty("type").GetProperty("kind").GetString() == "int64" &&
    root.GetProperty("dtos")[2].GetProperty("fields")[1].GetProperty("type").GetProperty("kind").GetString() == "string" &&
    root.GetProperty("dtos")[2].GetProperty("fields")[2].GetProperty("type").GetProperty("kind").GetString() == "int32",
    "Canonical OrderDraft/OrderReceipt DTO shapes were not emitted in stable field order.");
var schemaHash = root.GetProperty("schemaHash").GetString()!;
var hashInput = ReadSchema(generated).Replace($"\"schemaHash\":\"{schemaHash}\"", "\"schemaHash\":null", StringComparison.Ordinal);
Require(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(hashInput)))
        .ToLowerInvariant() == schemaHash, "Schema hash does not match the canonical schema.");

var typedReplySource = source.Replace(
    "[LiftNativeService(\"confirmation\", Id = 3, Version = 1)]",
    "[LiftNativeService(\"confirmation\", Id = 3, Version = 1, ReplyProtocol = LiftNativeReplyProtocol.TypedV1)]",
    StringComparison.Ordinal);
var (typedReplyGenerated, typedReplyCompilation, typedReplyDiagnostics) = Generate(typedReplySource);
Require(!typedReplyDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) &&
    !typedReplyCompilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
    "Typed reply contract did not compile.");
using var typedReplyJson = JsonDocument.Parse(ReadSchema(typedReplyGenerated));
var typedReplyHash = typedReplyJson.RootElement.GetProperty("schemaHash").GetString()!;
var typedReplyHashInput = ReadSchema(typedReplyGenerated)
    .Replace($"\"schemaHash\":\"{typedReplyHash}\"", "\"schemaHash\":null", StringComparison.Ordinal);
Require(typedReplyJson.RootElement.GetProperty("schemaVersion").GetInt32() == 3 &&
    typedReplyJson.RootElement.GetProperty("services")[1].GetProperty("replyProtocol").GetString() == "typed-v1" &&
    typedReplyGenerated != generated &&
    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(typedReplyHashInput))).ToLowerInvariant() == typedReplyHash,
    "Typed reply opt-in did not emit schema v3 with hash-covered reply metadata.");
var typedReplyProjections = ProjectionEmitter.Emit(typedReplyGenerated);
var typedReplyOutput = Path.Combine(AppContext.BaseDirectory, "projection-typed-reply");
Directory.CreateDirectory(typedReplyOutput);
foreach (var output in typedReplyProjections)
    File.WriteAllText(Path.Combine(typedReplyOutput, output.Key), output.Value);
File.WriteAllText(Path.Combine(typedReplyOutput, "module.modulemap"),
    "module NetLiftNativeHostABI {\n    header \"netlift.h\"\n    export *\n}\n");
Require(Fingerprint(typedReplyProjections["netlift.h"]) ==
        "8c5e9e59490a0e2e12163d62689af852f417c35171ac83b7e3323d59c74850dd" &&
    Fingerprint(typedReplyProjections["NetLift.swift"]) ==
        "a59709c097499063440de1e2ffaad32bc7554e32315db73cd3adf97a6b763485" &&
    Fingerprint(typedReplyProjections["NetLift.kt"]) ==
        "7b402c4c5c09cc2c5eca585d0a624034983b83e9d6cbdeb6c06f8ccc8b6328f7",
    $"Schema v3 C/Swift/Kotlin projections changed byte-for-byte: " +
    $"{Fingerprint(typedReplyProjections["netlift.h"])} " +
    $"{Fingerprint(typedReplyProjections["NetLift.swift"])} " +
    $"{Fingerprint(typedReplyProjections["NetLift.kt"])}.");
Require(typedReplyProjections["netlift.h"].Contains("LIFT_FEATURE_TYPED_NATIVE_REPLY 0x00000004u", StringComparison.Ordinal) &&
    typedReplyProjections["netlift.h"].Contains("LIFT_ENTRYPOINT_REPLY_TYPED \"ReplyTyped\"", StringComparison.Ordinal) &&
    typedReplyProjections["netlift.h"].Contains("lift_native_reply", StringComparison.Ordinal) &&
    typedReplyProjections["NetLift.swift"].Contains("LIFT_FEATURE_TYPED_NATIVE_REPLY", StringComparison.Ordinal) &&
    typedReplyProjections["NetLift.swift"].Contains("replyTyped: @escaping", StringComparison.Ordinal) &&
    typedReplyProjections["NetLift.kt"].Contains("LiftNativeTypedReplyAbi", StringComparison.Ordinal) &&
    typedReplyProjections["NetLift.kt"].Contains("FEATURE_TYPED_NATIVE_REPLY = 4", StringComparison.Ordinal) &&
    typedReplyProjections["NetLift.kt"].Contains("Typed native reply ABI feature is unavailable.", StringComparison.Ordinal) &&
    typedReplyProjections["NetLift.kt"].Contains("LiftNativeReplyOutcome.HANDLER_FAILURE", StringComparison.Ordinal),
    "Schema v3 typed-reply status and feature-gated projection surfaces were incomplete.");
var releaseSafeTypedReplyProjections = ProjectionEmitter.Emit(
    typedReplyGenerated,
    new ProjectionOptions(DropLateCallbackRepliesAfterRelease: true));
Require(releaseSafeTypedReplyProjections["netlift.h"] == typedReplyProjections["netlift.h"] &&
    releaseSafeTypedReplyProjections["NetLift.swift"] == typedReplyProjections["NetLift.swift"] &&
    releaseSafeTypedReplyProjections["NetLift.kt"] != typedReplyProjections["NetLift.kt"] &&
    releaseSafeTypedReplyProjections["NetLift.kt"].Contains(
        "Native release cancels callback jobs without joining them; late replies are discarded.",
        StringComparison.Ordinal) &&
    releaseSafeTypedReplyProjections["NetLift.kt"].Contains(
        "if (!synchronized(gate) { released })",
        StringComparison.Ordinal),
    "The opt-in Kotlin release lifecycle projection changed unrelated projections or omitted release-safe callback handling.");
var releaseSafeTypedReplyOutput = Path.Combine(AppContext.BaseDirectory, "projection-typed-reply-release-safe");
Directory.CreateDirectory(releaseSafeTypedReplyOutput);
foreach (var output in releaseSafeTypedReplyProjections)
    File.WriteAllText(Path.Combine(releaseSafeTypedReplyOutput, output.Key), output.Value);

var lifecycleV2Projections = ProjectionEmitter.Emit(
    typedReplyGenerated,
    new ProjectionOptions(KotlinCallbackLifecycleV2: true));
Require(lifecycleV2Projections["netlift.h"] == typedReplyProjections["netlift.h"] &&
    lifecycleV2Projections["NetLift.swift"] == typedReplyProjections["NetLift.swift"] &&
    lifecycleV2Projections["NetLift.kt"] != typedReplyProjections["NetLift.kt"] &&
    lifecycleV2Projections["NetLift.kt"].Contains(
        "interface LiftNativeCallbacksV2 : LiftNativeCallbacks",
        StringComparison.Ordinal) &&
    lifecycleV2Projections["NetLift.kt"].Contains(
        "data class LiftNativeRegistrationV2(val token: Long, val generation: Long)",
        StringComparison.Ordinal) &&
    lifecycleV2Projections["NetLift.kt"].Contains(
        "fun ackDrain(registrationToken: Long, generation: Long)",
        StringComparison.Ordinal) &&
    lifecycleV2Projections["NetLift.kt"].Contains(
        "fun revokeV2(registrationToken: Long)", StringComparison.Ordinal) &&
    lifecycleV2Projections["NetLift.kt"].Contains("public fun requestClose()", StringComparison.Ordinal) &&
    lifecycleV2Projections["NetLift.kt"].Contains("public suspend fun awaitClosed()", StringComparison.Ordinal) &&
    lifecycleV2Projections["NetLift.kt"].Contains("RequestPhase { OPEN, REPLY_CLAIMED, REVOKED }", StringComparison.Ordinal),
    "The opt-in Kotlin callback lifecycle v2 surface or serialized request state was not emitted.");
var lifecycleV2Output = Path.Combine(AppContext.BaseDirectory, "projection-typed-reply-lifecycle-v2");
Directory.CreateDirectory(lifecycleV2Output);
foreach (var output in lifecycleV2Projections)
    File.WriteAllText(Path.Combine(lifecycleV2Output, output.Key), output.Value);

var swiftCallbackV2Source = typedReplySource.Replace(
    "[property: LiftField(12)] string? Optional",
    "[property: LiftField(12)] string Optional",
    StringComparison.Ordinal);
var (swiftCallbackV2Generated, _, swiftCallbackV2Diagnostics) =
    Generate(swiftCallbackV2Source, "NetLiftSwiftCallbackLifecycleV2Fixture");
Require(!swiftCallbackV2Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
    "Swift callback lifecycle V2 fixture did not generate.");
var swiftCallbackV2DefaultProjections = ProjectionEmitter.Emit(swiftCallbackV2Generated);
var swiftCallbackV2Projections = ProjectionEmitter.Emit(
    swiftCallbackV2Generated,
    new ProjectionOptions(SwiftNativeCallbackLifecycleV2: true));
var swiftCallbackV2ProjectionsAgain = ProjectionEmitter.Emit(
    swiftCallbackV2Generated,
    new ProjectionOptions(SwiftNativeCallbackLifecycleV2: true));
Require(swiftCallbackV2Projections.SequenceEqual(swiftCallbackV2ProjectionsAgain) &&
    swiftCallbackV2Projections["netlift.h"] == swiftCallbackV2DefaultProjections["netlift.h"] &&
    swiftCallbackV2Projections["NetLift.kt"] == swiftCallbackV2DefaultProjections["NetLift.kt"] &&
    swiftCallbackV2Projections["NetLift.swift"] != swiftCallbackV2DefaultProjections["NetLift.swift"] &&
    swiftCallbackV2Projections["NetLift.swift"].Contains(
        "NetLiftNativeCallbackRegistrationV2_1", StringComparison.Ordinal) &&
    swiftCallbackV2Projections["NetLift.swift"].Contains(
        "netlift_coreclr_get_function_table_v2", StringComparison.Ordinal) &&
    swiftCallbackV2Projections["NetLift.swift"].Contains(
        "LIFT_FEATURE_CALLBACK_LIFECYCLE_V2", StringComparison.Ordinal) &&
    swiftCallbackV2Projections["NetLift.swift"].Contains(
        "cancel_request", StringComparison.Ordinal) &&
    !swiftCallbackV2Projections["NetLift.swift"].Contains(
        "NetLiftAsyncState_1", StringComparison.Ordinal) &&
    !swiftCallbackV2Projections["netlift.h"].Contains(
        "LIFT_FEATURE_CALLBACK_LIFECYCLE_V2", StringComparison.Ordinal),
    "Opt-in Swift callback lifecycle V2 was not deterministic, isolated from V1/Kotlin, or feature-unadvertised.");
var swiftCallbackV2Output = Path.Combine(
    AppContext.BaseDirectory,
    "projection-swift-native-callback-v2");
Directory.CreateDirectory(swiftCallbackV2Output);
foreach (var output in swiftCallbackV2Projections)
    File.WriteAllText(Path.Combine(swiftCallbackV2Output, output.Key), output.Value);
var swiftCallbackV2Header = Path.Combine(
    Directory.GetCurrentDirectory(),
    "spikes/apple/NativeSwiftHost/typed/NetLiftCallbackLifecycleV2.h");
File.Copy(swiftCallbackV2Header,
    Path.Combine(swiftCallbackV2Output, "NetLiftCallbackLifecycleV2.h"), overwrite: true);
File.Copy(
    Path.Combine(Directory.GetCurrentDirectory(), "tests/NetLift.Tests/NetLiftCallbackLifecycleV2Fake.h"),
    Path.Combine(swiftCallbackV2Output, "NetLiftCallbackLifecycleV2Fake.h"), overwrite: true);
var generatedTypedDirectory = Path.Combine(swiftCallbackV2Output, "GeneratedTyped");
Directory.CreateDirectory(generatedTypedDirectory);
File.WriteAllText(
    Path.Combine(generatedTypedDirectory, "netlift.h"),
    swiftCallbackV2Projections["netlift.h"]);
File.WriteAllText(
    Path.Combine(swiftCallbackV2Output, "module.modulemap"),
    "module NetLiftNativeHostABI {\n" +
    "    header \"netlift.h\"\n" +
    "    header \"NetLiftCallbackLifecycleV2.h\"\n" +
    "    header \"NetLiftCallbackLifecycleV2Fake.h\"\n" +
    "    export *\n}\n");

var swiftCallbackV2KeywordSource = swiftCallbackV2Source.Replace(
    "Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken);",
    "Task<bool> @class(string @switch, CancellationToken cancellationToken);",
    StringComparison.Ordinal);
var (swiftCallbackV2KeywordGenerated, _, swiftCallbackV2KeywordDiagnostics) =
    Generate(swiftCallbackV2KeywordSource, "NetLiftSwiftCallbackLifecycleV2KeywordFixture");
Require(!swiftCallbackV2KeywordDiagnostics.Any(diagnostic =>
        diagnostic.Severity == DiagnosticSeverity.Error),
    "Swift callback lifecycle V2 native keyword fixture did not generate.");
var swiftCallbackV2KeywordProjections = ProjectionEmitter.Emit(
    swiftCallbackV2KeywordGenerated,
    new ProjectionOptions(SwiftNativeCallbackLifecycleV2: true));
Require(swiftCallbackV2KeywordProjections["NetLift.swift"].Contains(
        "func `class`(`switch`: String) async throws -> Bool", StringComparison.Ordinal) &&
    swiftCallbackV2KeywordProjections["NetLift.swift"].Contains(
        "handler.`class`(switch: prompt)", StringComparison.Ordinal),
    "Swift callback lifecycle V2 did not preserve escaped native method/parameter names.");
var swiftCallbackV2KeywordOutput = Path.Combine(swiftCallbackV2Output, "keyword");
Directory.CreateDirectory(swiftCallbackV2KeywordOutput);
foreach (var output in swiftCallbackV2KeywordProjections)
    File.WriteAllText(Path.Combine(swiftCallbackV2KeywordOutput, output.Key), output.Value);
File.Copy(swiftCallbackV2Header,
    Path.Combine(swiftCallbackV2KeywordOutput, "NetLiftCallbackLifecycleV2.h"), overwrite: true);
File.Copy(
    Path.Combine(Directory.GetCurrentDirectory(), "tests/NetLift.Tests/NetLiftCallbackLifecycleV2Fake.h"),
    Path.Combine(swiftCallbackV2KeywordOutput, "NetLiftCallbackLifecycleV2Fake.h"), overwrite: true);
var keywordGeneratedTypedDirectory = Path.Combine(swiftCallbackV2KeywordOutput, "GeneratedTyped");
Directory.CreateDirectory(keywordGeneratedTypedDirectory);
File.WriteAllText(
    Path.Combine(keywordGeneratedTypedDirectory, "netlift.h"),
    swiftCallbackV2KeywordProjections["netlift.h"]);
File.WriteAllText(
    Path.Combine(swiftCallbackV2KeywordOutput, "module.modulemap"),
    "module NetLiftNativeHostABI {\n" +
    "    header \"netlift.h\"\n" +
    "    header \"NetLiftCallbackLifecycleV2.h\"\n" +
    "    header \"NetLiftCallbackLifecycleV2Fake.h\"\n" +
    "    export *\n}\n");

var swiftCallbackV2RequiresTypedReply = false;
try
{
    var noTypedReplySource = swiftCallbackV2Source.Replace(
        "[LiftNativeService(\"confirmation\", Id = 3, Version = 1, ReplyProtocol = LiftNativeReplyProtocol.TypedV1)]",
        "[LiftNativeService(\"confirmation\", Id = 3, Version = 1)]",
        StringComparison.Ordinal);
    var (noTypedReplyGenerated, _, noTypedReplyDiagnostics) =
        Generate(noTypedReplySource, "NetLiftSwiftCallbackLifecycleV2NoTypedReplyFixture");
    Require(!noTypedReplyDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
        "Swift callback lifecycle V2 negative fixture did not generate.");
    _ = ProjectionEmitter.Emit(noTypedReplyGenerated,
        new ProjectionOptions(SwiftNativeCallbackLifecycleV2: true));
}
catch (InvalidDataException exception) when (
    exception.Message.Contains("typed native replies", StringComparison.Ordinal))
{
    swiftCallbackV2RequiresTypedReply = true;
}
Require(swiftCallbackV2RequiresTypedReply,
    "Swift callback lifecycle V2 was not rejected when typed native replies were absent.");

var lifecycleV2RequiresTypedReply = false;
try
{
    _ = ProjectionEmitter.Emit(generated, new ProjectionOptions(KotlinCallbackLifecycleV2: true));
}
catch (InvalidDataException exception) when (exception.Message.Contains("typed native reply", StringComparison.Ordinal))
{
    lifecycleV2RequiresTypedReply = true;
}
Require(lifecycleV2RequiresTypedReply,
    "Kotlin callback lifecycle v2 was not rejected for a contract without typed native replies.");

var flatCodecSource = """
    #nullable enable
    using System.Threading;
    using System.Threading.Tasks;
    using NetLift;

    [LiftDto("dispatch-request", Id = 31, Version = 1)]
    public sealed record DispatchRequest(
        [property: LiftField(1)] string Channel,
        [property: LiftField(2)] long Sequence);

    [LiftDto("dispatch-receipt", Id = 32, Version = 1)]
    public sealed record DispatchReceipt(
        [property: LiftField(1)] long DeliveryId,
        [property: LiftField(2)] string Channel,
        [property: LiftField(3)] long Sequence,
        [property: LiftField(4)] bool Approved);

    [LiftDto("fixed-width-values", Id = 44, Version = 1)]
    public sealed record FixedWidthValues(
        [property: LiftField(1)] bool Flag,
        [property: LiftField(2)] byte UnsignedByte,
        [property: LiftField(3)] sbyte SignedByte,
        [property: LiftField(4)] short SignedShort,
        [property: LiftField(5)] ushort UnsignedShort,
        [property: LiftField(6)] int SignedInt,
        [property: LiftField(7)] uint UnsignedInt,
        [property: LiftField(8)] long SignedLong,
        [property: LiftField(9)] ulong UnsignedLong,
        [property: LiftField(10)] float Float32,
        [property: LiftField(11)] double Float64);

    [LiftService("dispatch", Id = 41, Version = 1)]
    public interface IDispatcher
    {
        [LiftMethod(1)]
        Task<DispatchReceipt> DispatchAsync(
            DispatchRequest request,
            CancellationToken cancellationToken);
    }
    """;
var (flatCodecGenerated, flatCodecCompilation, flatCodecDiagnostics) = Generate(flatCodecSource);
Require(!flatCodecDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) &&
    !flatCodecCompilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
    "Second-app flat DTO contract did not compile.");
var flatCodecDefaultProjection = ProjectionEmitter.Emit(flatCodecGenerated);
var flatCodecProjections = ProjectionEmitter.Emit(
    flatCodecGenerated,
    new ProjectionOptions(EmitKotlinFlatCodecs: true));
Require(flatCodecProjections["netlift.h"] == flatCodecDefaultProjection["netlift.h"] &&
    flatCodecProjections["NetLift.swift"] == flatCodecDefaultProjection["NetLift.swift"] &&
    flatCodecProjections["NetLift.kt"] != flatCodecDefaultProjection["NetLift.kt"] &&
    !flatCodecDefaultProjection["NetLift.kt"].Contains("NetLiftFlatDtoWire", StringComparison.Ordinal) &&
    flatCodecProjections["NetLift.kt"].Contains(
        "public fun encodeDispatchRequest(value: DispatchRequest): ByteArray",
        StringComparison.Ordinal) &&
    flatCodecProjections["NetLift.kt"].Contains(
        "public fun decodeDispatchReceipt(source: ByteArray): DispatchReceipt",
        StringComparison.Ordinal) &&
    flatCodecProjections["NetLift.kt"].Contains("Malformed UTF-8 DTO field.", StringComparison.Ordinal) &&
    flatCodecProjections["NetLift.kt"].Contains("Boolean DTO field must be encoded as 0 or 1.", StringComparison.Ordinal) &&
    flatCodecProjections["NetLift.kt"].Contains("Trailing DTO bytes.", StringComparison.Ordinal),
    "Opt-in Kotlin flat DTO codecs omitted typed functions or strict wire checks, or changed unrelated projections.");
var flatCodecOutput = Path.Combine(AppContext.BaseDirectory, "projection-flat-codec-tests");
Directory.CreateDirectory(flatCodecOutput);
File.WriteAllText(Path.Combine(flatCodecOutput, "Contract.g.cs"), flatCodecGenerated);
foreach (var output in flatCodecProjections)
    File.WriteAllText(Path.Combine(flatCodecOutput, output.Key), output.Value);

var kotlinServiceFacadeSource = """
    #nullable enable
    using System.Threading;
    using System.Threading.Tasks;
    using NetLift;

    [LiftDto("dispatch-request", Id = 31, Version = 1)]
    public sealed record DispatchRequest(
        [property: LiftField(1)] string Channel,
        [property: LiftField(2)] long Sequence);

    [LiftDto("dispatch-receipt", Id = 32, Version = 1)]
    public sealed record DispatchReceipt(
        [property: LiftField(1)] long DeliveryId,
        [property: LiftField(2)] string Channel,
        [property: LiftField(3)] long Sequence,
        [property: LiftField(4)] bool Approved);

    [LiftService("dispatch", Id = 41, Version = 1)]
    public interface IDispatchService
    {
        [LiftMethod(1)]
        Task<DispatchReceipt> DispatchAsync(
            DispatchRequest request,
            CancellationToken cancellationToken);
    }

    [LiftNativeService(
        "approval",
        Id = 42,
        Version = 1,
        ReplyProtocol = LiftNativeReplyProtocol.TypedV1)]
    public interface IApprovalService
    {
        [LiftMethod(1)]
        Task<bool> ApproveAsync(string prompt, CancellationToken cancellationToken);
    }
    """;
var (kotlinServiceFacadeGenerated, kotlinServiceFacadeCompilation, kotlinServiceFacadeDiagnostics) =
    Generate(kotlinServiceFacadeSource, "NetLiftKotlinGeneratedServiceFacadeV2Fixture");
Require(!kotlinServiceFacadeDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) &&
    !kotlinServiceFacadeCompilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
    "Second-contract Kotlin generated service facade fixture did not compile.");
var kotlinServiceFacadeDefault = ProjectionEmitter.Emit(kotlinServiceFacadeGenerated);
var kotlinServiceFacadeProjection = ProjectionEmitter.Emit(
    kotlinServiceFacadeGenerated,
    new ProjectionOptions(KotlinGeneratedServiceFacadeV2: true));
var kotlinServiceFacadeProjectionAgain = ProjectionEmitter.Emit(
    kotlinServiceFacadeGenerated,
    new ProjectionOptions(KotlinGeneratedServiceFacadeV2: true));
Require(kotlinServiceFacadeProjection.SequenceEqual(kotlinServiceFacadeProjectionAgain) &&
    kotlinServiceFacadeProjection["netlift.h"] == kotlinServiceFacadeDefault["netlift.h"] &&
    kotlinServiceFacadeProjection["NetLift.swift"] == kotlinServiceFacadeDefault["NetLift.swift"] &&
    kotlinServiceFacadeProjection["NetLift.kt"] != kotlinServiceFacadeDefault["NetLift.kt"] &&
    !kotlinServiceFacadeDefault["NetLift.kt"].Contains("NetLiftFlatDtoWire", StringComparison.Ordinal) &&
    kotlinServiceFacadeProjection["NetLift.kt"].Contains(
        "public class DispatchClient(", StringComparison.Ordinal) &&
    kotlinServiceFacadeProjection["NetLift.kt"].Contains(
        "public suspend fun dispatchAsync(request: DispatchRequest): DispatchReceipt",
        StringComparison.Ordinal) &&
    kotlinServiceFacadeProjection["NetLift.kt"].Contains(
        "NetLiftFlatDtoWire.encodeDispatchRequest(request)", StringComparison.Ordinal) &&
    kotlinServiceFacadeProjection["NetLift.kt"].Contains(
        "NetLiftFlatDtoWire.decodeDispatchReceipt(result)", StringComparison.Ordinal) &&
    kotlinServiceFacadeProjection["NetLift.kt"].Contains(
        "NetLiftFlatDtoWire.decodeStringField(payload, 1)", StringComparison.Ordinal) &&
    kotlinServiceFacadeProjection["NetLift.kt"].Contains(
        "if (pending.containsKey(operationToken))", StringComparison.Ordinal) &&
    kotlinServiceFacadeProjection["NetLift.kt"].Contains(
        "operationToken != 0L && !duplicateOperationToken", StringComparison.Ordinal) &&
    kotlinServiceFacadeProjection["NetLift.kt"].Contains(
        "private val onApproval: suspend (String) -> Boolean", StringComparison.Ordinal) &&
    kotlinServiceFacadeProjection["NetLift.kt"].Contains(
        "88f5ad28f00ea09ae0c0cb9850f27ca8bad7634757160232a30d0426149d0c5f",
        StringComparison.Ordinal) &&
    kotlinServiceFacadeProjection["NetLift.kt"].Contains(
        "public fun requestClose()", StringComparison.Ordinal) &&
    kotlinServiceFacadeProjection["NetLift.kt"].Contains(
        "Callback handlers must call requestClose() and leave awaiting to an external owner.",
        StringComparison.Ordinal) &&
    !kotlinServiceFacadeProjection["NetLift.kt"].Contains(
        "NetLiftOrdersWire", StringComparison.Ordinal) &&
    !kotlinServiceFacadeProjection["NetLift.kt"].Contains(
        "OrdersClient", StringComparison.Ordinal),
    "Opt-in Kotlin generated service facade was nondeterministic, Orders-specific, or changed unrelated projections.");
var kotlinServiceFacadeOutput = Path.Combine(
    AppContext.BaseDirectory,
    "projection-kotlin-generated-service-facade-v2");
Directory.CreateDirectory(kotlinServiceFacadeOutput);
foreach (var output in kotlinServiceFacadeProjection)
    File.WriteAllText(Path.Combine(kotlinServiceFacadeOutput, output.Key), output.Value);
File.WriteAllText(
    Path.Combine(kotlinServiceFacadeOutput, "Contract.g.cs"),
    kotlinServiceFacadeGenerated);

var kotlinServiceFacadeCollisionSource = kotlinServiceFacadeSource.Replace(
    "[LiftService(\"dispatch\", Id = 41, Version = 1)]",
    """
    [LiftDto("dispatch-client", Id = 45, Version = 1)]
    public sealed record DispatchClient(
        [property: LiftField(1)] bool @when,
        [property: LiftField(2)] bool @class);

    [LiftDto("NetLiftDispatchContract", Id = 46, Version = 1)]
    public sealed record NetLiftDispatchContract([property: LiftField(1)] int Value);

    [LiftService("dispatch", Id = 41, Version = 1)]
    """,
    StringComparison.Ordinal).Replace(
    "Task<bool> ApproveAsync(string prompt, CancellationToken cancellationToken);",
    "Task<bool> @class(string @switch, CancellationToken cancellationToken);",
    StringComparison.Ordinal);
var (kotlinServiceFacadeCollisionGenerated, _, kotlinServiceFacadeCollisionDiagnostics) =
    Generate(kotlinServiceFacadeCollisionSource, "NetLiftKotlinGeneratedServiceFacadeV2CollisionFixture");
Require(!kotlinServiceFacadeCollisionDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
    "Kotlin generated service facade name-collision fixture did not generate.");
var kotlinServiceFacadeCollisionProjection = ProjectionEmitter.Emit(
    kotlinServiceFacadeCollisionGenerated,
    new ProjectionOptions(KotlinGeneratedServiceFacadeV2: true));
var kotlinServiceFacadeCollisionProjectionAgain = ProjectionEmitter.Emit(
    kotlinServiceFacadeCollisionGenerated,
    new ProjectionOptions(KotlinGeneratedServiceFacadeV2: true));
var kotlinServiceFacadeCollisionOutput = Path.Combine(
    AppContext.BaseDirectory,
    "projection-kotlin-generated-service-facade-v2-collisions");
Directory.CreateDirectory(kotlinServiceFacadeCollisionOutput);
foreach (var output in kotlinServiceFacadeCollisionProjection)
    File.WriteAllText(Path.Combine(kotlinServiceFacadeCollisionOutput, output.Key), output.Value);
Require(kotlinServiceFacadeCollisionProjection["NetLift.kt"] ==
        kotlinServiceFacadeCollisionProjectionAgain["NetLift.kt"] &&
    kotlinServiceFacadeCollisionProjection["NetLift.kt"].Contains(
        "data class DispatchClient_dto_45", StringComparison.Ordinal) &&
    kotlinServiceFacadeCollisionProjection["NetLift.kt"].Contains(
        "data class NetLiftDispatchContract_dto_46", StringComparison.Ordinal) &&
    kotlinServiceFacadeCollisionProjection["NetLift.kt"].Contains(
        "val `when`: Boolean", StringComparison.Ordinal) &&
    kotlinServiceFacadeCollisionProjection["NetLift.kt"].Contains(
        "val `class`: Boolean", StringComparison.Ordinal) &&
    kotlinServiceFacadeCollisionProjection["NetLift.kt"].Contains(
        "suspend fun `class`(switch: String): Boolean", StringComparison.Ordinal),
    "Kotlin generated service facade did not deterministically escape keywords and reserve generated type names.");

var kotlinServiceFacadeRequiresTypedReply = false;
var kotlinServiceFacadeUntypedSource = kotlinServiceFacadeSource.Replace(
    """
    [LiftNativeService(
        "approval",
        Id = 42,
        Version = 1,
        ReplyProtocol = LiftNativeReplyProtocol.TypedV1)]
    """,
    "[LiftNativeService(\"approval\", Id = 42, Version = 1)]",
    StringComparison.Ordinal);
var (kotlinServiceFacadeUntypedGenerated, _, kotlinServiceFacadeUntypedDiagnostics) =
    Generate(kotlinServiceFacadeUntypedSource, "NetLiftKotlinGeneratedServiceFacadeV2UntypedFixture");
Require(!kotlinServiceFacadeUntypedDiagnostics.Any(diagnostic =>
        diagnostic.Severity == DiagnosticSeverity.Error),
    "Untyped Kotlin service facade negative fixture did not generate.");
try
{
    _ = ProjectionEmitter.Emit(
        kotlinServiceFacadeUntypedGenerated,
        new ProjectionOptions(KotlinGeneratedServiceFacadeV2: true));
}
catch (InvalidDataException exception) when (
    exception.Message.Contains("typed native replies", StringComparison.Ordinal))
{
    kotlinServiceFacadeRequiresTypedReply = true;
}
Require(kotlinServiceFacadeRequiresTypedReply,
    "Kotlin generated service facade accepted a native service without typed replies.");

var kotlinServiceFacadeNullableSource = kotlinServiceFacadeSource.Replace(
    "[property: LiftField(1)] string Channel",
    "[property: LiftField(1)] string? Channel",
    StringComparison.Ordinal);
var (kotlinServiceFacadeNullableGenerated, _, kotlinServiceFacadeNullableDiagnostics) =
    Generate(kotlinServiceFacadeNullableSource, "NetLiftKotlinGeneratedServiceFacadeV2NullableFixture");
Require(!kotlinServiceFacadeNullableDiagnostics.Any(diagnostic =>
        diagnostic.Severity == DiagnosticSeverity.Error),
    "Nullable Kotlin service facade negative fixture did not generate.");
var kotlinServiceFacadeRejectsNullableDto = false;
try
{
    _ = ProjectionEmitter.Emit(
        kotlinServiceFacadeNullableGenerated,
        new ProjectionOptions(KotlinGeneratedServiceFacadeV2: true));
}
catch (InvalidDataException exception) when (
    exception.Message.Contains("dispatch-request.Channel", StringComparison.Ordinal) &&
    exception.Message.Contains("nullable", StringComparison.Ordinal))
{
    kotlinServiceFacadeRejectsNullableDto = true;
}
Require(kotlinServiceFacadeRejectsNullableDto,
    "Kotlin generated service facade silently accepted a nullable flat DTO field.");

var collisionFlatDtoSource = flatCodecSource
    .Replace("dispatch-receipt", "dispatch_request", StringComparison.Ordinal)
    .Replace(
        "[LiftService(\"dispatch\", Id = 41, Version = 1)]",
        """
        [LiftDto("NetLiftFlatDtoWire", Id = 43, Version = 1)]
        public sealed record CodecObjectCollision([property: LiftField(1)] bool Ready);

        [LiftService("dispatch", Id = 40, Version = 1)]
        """,
        StringComparison.Ordinal);
var (collisionFlatDtoGenerated, _, collisionFlatDtoDiagnostics) = Generate(collisionFlatDtoSource);
Require(!collisionFlatDtoDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
    "Kotlin flat DTO codec collision fixture did not generate.");
var collisionFlatDtoProjection = ProjectionEmitter.Emit(
    collisionFlatDtoGenerated,
    new ProjectionOptions(EmitKotlinFlatCodecs: true));
var collisionFlatDtoProjectionAgain = ProjectionEmitter.Emit(
    collisionFlatDtoGenerated,
    new ProjectionOptions(EmitKotlinFlatCodecs: true));
Require(collisionFlatDtoProjection["NetLift.kt"] == collisionFlatDtoProjectionAgain["NetLift.kt"] &&
    collisionFlatDtoProjection["NetLift.kt"].Contains(
        "encodeDispatchRequest_dto_31",
        StringComparison.Ordinal) &&
    collisionFlatDtoProjection["NetLift.kt"].Contains(
        "encodeDispatchRequest_dto_32",
        StringComparison.Ordinal) &&
    collisionFlatDtoProjection["NetLift.kt"].Contains(
        "data class NetLiftFlatDtoWire_dto_43",
        StringComparison.Ordinal) &&
    collisionFlatDtoProjection["NetLift.kt"].Contains(
        "public object NetLiftFlatDtoWire",
        StringComparison.Ordinal),
    "Kotlin flat DTO codec names were not deterministic or collision-safe.");

var nullableFlatDtoSource = flatCodecSource.Replace(
    "[property: LiftField(1)] string Channel",
    "[property: LiftField(1)] string? Channel",
    StringComparison.Ordinal);
var (nullableFlatDtoGenerated, _, nullableFlatDtoDiagnostics) = Generate(nullableFlatDtoSource);
Require(!nullableFlatDtoDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
    "Nullable second-app DTO source did not produce a schema for the explicit unsupported-shape test.");
File.WriteAllText(Path.Combine(flatCodecOutput, "NullableContract.g.cs"), nullableFlatDtoGenerated);
var nullableFlatDtoRejected = false;
try
{
    _ = ProjectionEmitter.Emit(nullableFlatDtoGenerated, new ProjectionOptions(EmitKotlinFlatCodecs: true));
}
catch (InvalidDataException exception) when (
    exception.Message.Contains("dispatch-request.Channel", StringComparison.Ordinal) &&
    exception.Message.Contains("nullable", StringComparison.Ordinal))
{
    nullableFlatDtoRejected = true;
}
Require(nullableFlatDtoRejected,
    "Opt-in Kotlin flat DTO codec emission silently omitted a nullable field.");

var swiftFlatCodecSource = """
    #nullable enable
    using System.Threading;
    using System.Threading.Tasks;
    using NetLift;

    [LiftDto("dispatch-command", Id = 61, Version = 1)]
    public sealed record DispatchCommand(
        [property: LiftField(1)] int PackageCount,
        [property: LiftField(2)] int Destination);

    [LiftDto("dispatch-receipt", Id = 62, Version = 1)]
    public sealed record DispatchReceipt(
        [property: LiftField(1)] long ReceiptId,
        [property: LiftField(2)] int PackageCount,
        [property: LiftField(3)] int Destination,
        [property: LiftField(4)] string TrackingCode);

    [LiftDto("dispatch-note", Id = 63, Version = 1)]
    public sealed record DispatchNote([property: LiftField(1)] string Message);

    [LiftDto("fixed-width-values", Id = 64, Version = 1)]
    public sealed record FixedWidthValues(
        [property: LiftField(1)] bool Flag,
        [property: LiftField(2)] byte UnsignedByte,
        [property: LiftField(3)] sbyte SignedByte,
        [property: LiftField(4)] short SignedShort,
        [property: LiftField(5)] ushort UnsignedShort,
        [property: LiftField(6)] int SignedInt,
        [property: LiftField(7)] uint UnsignedInt,
        [property: LiftField(8)] long SignedLong,
        [property: LiftField(9)] ulong UnsignedLong,
        [property: LiftField(10)] float Float32,
        [property: LiftField(11)] double Float64);

    [LiftService("dispatch", Id = 65, Version = 1)]
    public interface IDispatchService
    {
        [LiftMethod(1)]
        Task<DispatchReceipt> DispatchAsync(
            DispatchCommand command,
            CancellationToken cancellationToken);
    }
    """;
var (swiftFlatCodecGenerated, swiftFlatCodecCompilation, swiftFlatCodecDiagnostics) =
    Generate(swiftFlatCodecSource, "NetLiftSwiftFlatCodecFixture");
Require(!swiftFlatCodecDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) &&
    !swiftFlatCodecCompilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
    "DispatchDemo Swift flat DTO contract did not compile.");
var swiftFlatCodecDefaultProjection = ProjectionEmitter.Emit(swiftFlatCodecGenerated);
var swiftFlatCodecProjection = ProjectionEmitter.Emit(
    swiftFlatCodecGenerated,
    new ProjectionOptions(EmitSwiftFlatCodecs: true));
Require(swiftFlatCodecProjection["netlift.h"] == swiftFlatCodecDefaultProjection["netlift.h"] &&
    swiftFlatCodecProjection["NetLift.kt"] == swiftFlatCodecDefaultProjection["NetLift.kt"] &&
    swiftFlatCodecProjection["NetLift.swift"] != swiftFlatCodecDefaultProjection["NetLift.swift"] &&
    swiftFlatCodecProjection["NetLift.swift"].Contains(
        "public enum NetLiftDtoCodec",
        StringComparison.Ordinal) &&
    swiftFlatCodecProjection["NetLift.swift"].Contains(
        "public enum NetLiftCodecError: Error, Equatable, Sendable",
        StringComparison.Ordinal) &&
    swiftFlatCodecProjection["NetLift.swift"].Contains(
        "public static func encodeDispatchReceipt(_ value: DispatchReceipt) throws -> [UInt8]",
        StringComparison.Ordinal) &&
    swiftFlatCodecProjection["NetLift.swift"].Contains(
        "public static func decodeDispatchReceipt(_ source: [UInt8]) throws -> DispatchReceipt",
        StringComparison.Ordinal),
    "Swift flat DTO codec opt-in omitted public typed codecs or changed unrelated projections.");
var swiftFlatCodecOutput = Path.Combine(AppContext.BaseDirectory, "projection-swift-flat-codec-tests");
Directory.CreateDirectory(swiftFlatCodecOutput);
File.WriteAllText(Path.Combine(swiftFlatCodecOutput, "Contract.g.cs"), swiftFlatCodecGenerated);
File.WriteAllText(Path.Combine(swiftFlatCodecOutput, "NetLift.swift"), swiftFlatCodecProjection["NetLift.swift"]);
using var swiftFlatCodecAssemblyStream = new MemoryStream();
var swiftFlatCodecEmit = swiftFlatCodecCompilation.Emit(swiftFlatCodecAssemblyStream);
Require(swiftFlatCodecEmit.Success, "Swift codec C# fixture could not be emitted: " +
    string.Join(Environment.NewLine, swiftFlatCodecEmit.Diagnostics.Where(diagnostic =>
        diagnostic.Severity == DiagnosticSeverity.Error)));
swiftFlatCodecAssemblyStream.Position = 0;
var swiftFlatCodecAssembly = AssemblyLoadContext.Default.LoadFromStream(swiftFlatCodecAssemblyStream);
foreach (var fixture in new[]
         {
             ("DispatchCommand", 61, new object?[] { 3, 2 }),
             ("DispatchReceipt", 62, new object?[] { 1L, 3, 2, "DSP-CENTRAL-000001" }),
             ("DispatchNote", 63, new object?[] { "Dispatch 🚚" }),
             ("FixedWidthValues", 64, new object?[]
             {
                 true, (byte)250, (sbyte)-12, (short)-1234, (ushort)54321, -1234567, 3456789012u,
                 -9000000000000L, 18000000000000UL, 1.5f, -2.25d
             })
         })
{
    var encoded = EncodeGeneratedDto(swiftFlatCodecAssembly, fixture.Item1,
        fixture.Item2, fixture.Item3);
    File.WriteAllBytes(Path.Combine(swiftFlatCodecOutput, fixture.Item1 + ".csharp.bin"), encoded);
}
Require(new FileInfo(Path.Combine(swiftFlatCodecOutput, "DispatchReceipt.csharp.bin")).Length == 58,
    "Generated C# DispatchReceipt wire fixture did not match the 58-byte app payload.");

var swiftFlatCodecCollisionSource = swiftFlatCodecSource.Replace(
    "[LiftService(\"dispatch\", Id = 65, Version = 1)]",
    """
    [LiftDto("dispatch_receipt", Id = 66, Version = 1)]
    public sealed record DispatchReceiptAlias([property: LiftField(1)] int Value);

    [LiftDto("NetLiftDtoCodec", Id = 67, Version = 1)]
    public sealed record CodecApiNameCollision([property: LiftField(1)] int Value);

    [LiftDto("NetLiftCodecError", Id = 68, Version = 1)]
    public sealed record CodecErrorNameCollision([property: LiftField(1)] int Value);

    [LiftService("dispatch", Id = 65, Version = 1)]
    """,
    StringComparison.Ordinal);
var (swiftFlatCodecCollisionGenerated, _, swiftFlatCodecCollisionDiagnostics) =
    Generate(swiftFlatCodecCollisionSource, "NetLiftSwiftFlatCodecCollisionFixture");
Require(!swiftFlatCodecCollisionDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
    "Swift flat DTO codec collision fixture did not generate.");
var swiftFlatCodecCollisionProjection = ProjectionEmitter.Emit(
    swiftFlatCodecCollisionGenerated,
    new ProjectionOptions(EmitSwiftFlatCodecs: true));
var swiftFlatCodecCollisionProjectionAgain = ProjectionEmitter.Emit(
    swiftFlatCodecCollisionGenerated,
    new ProjectionOptions(EmitSwiftFlatCodecs: true));
Require(swiftFlatCodecCollisionProjection["NetLift.swift"] ==
        swiftFlatCodecCollisionProjectionAgain["NetLift.swift"] &&
    swiftFlatCodecCollisionProjection["NetLift.swift"].Contains(
        "public static func encodeDispatchReceipt_dto_62(_ value: DispatchReceipt_dto_62)",
        StringComparison.Ordinal) &&
    swiftFlatCodecCollisionProjection["NetLift.swift"].Contains(
        "public static func encodeDispatchReceipt_dto_66(_ value: DispatchReceipt_dto_66)",
        StringComparison.Ordinal) &&
    swiftFlatCodecCollisionProjection["NetLift.swift"].Contains(
        "public struct NetLiftDtoCodec_dto_67",
        StringComparison.Ordinal) &&
    swiftFlatCodecCollisionProjection["NetLift.swift"].Contains(
        "public struct NetLiftCodecError_dto_68",
        StringComparison.Ordinal),
    "Swift flat DTO codec identifiers were not deterministic and collision-safe.");
var swiftFlatCodecCollisionOutput = Path.Combine(
    AppContext.BaseDirectory,
    "projection-swift-flat-codec-collision-tests");
Directory.CreateDirectory(swiftFlatCodecCollisionOutput);
File.WriteAllText(
    Path.Combine(swiftFlatCodecCollisionOutput, "NetLift.swift"),
    swiftFlatCodecCollisionProjection["NetLift.swift"]);

var nullableSwiftFlatDtoSource = swiftFlatCodecSource.Replace(
    "[property: LiftField(4)] string TrackingCode",
    "[property: LiftField(4)] string? TrackingCode",
    StringComparison.Ordinal);
var (nullableSwiftFlatDtoGenerated, _, nullableSwiftFlatDtoDiagnostics) =
    Generate(nullableSwiftFlatDtoSource, "NetLiftSwiftFlatCodecNullableFixture");
Require(!nullableSwiftFlatDtoDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
    "Nullable Swift codec fixture did not produce a schema for the unsupported-shape check.");
File.WriteAllText(Path.Combine(swiftFlatCodecOutput, "NullableContract.g.cs"), nullableSwiftFlatDtoGenerated);
var nullableSwiftFlatDtoRejected = false;
try
{
    _ = ProjectionEmitter.Emit(nullableSwiftFlatDtoGenerated, new ProjectionOptions(EmitSwiftFlatCodecs: true));
}
catch (InvalidDataException exception) when (
    exception.Message.Contains("dispatch-receipt.TrackingCode", StringComparison.Ordinal) &&
    exception.Message.Contains("nullable", StringComparison.Ordinal))
{
    nullableSwiftFlatDtoRejected = true;
}
Require(nullableSwiftFlatDtoRejected,
    "Swift flat DTO codec emission silently omitted a nullable field.");

var nestedSwiftFlatDtoGenerated = ReplaceCanonicalSchema(swiftFlatCodecGenerated, schema =>
{
    var receipt = schema["dtos"]!.AsArray()
        .Single(dto => dto!["id"]!.GetValue<int>() == 62)!;
    var field = receipt["fields"]!.AsArray()
        .Single(candidate => candidate!["id"]!.GetValue<int>() == 4)!;
    field["type"]!.AsObject()["kind"] = "dto";
});
var nestedSwiftFlatDtoRejected = false;
try
{
    _ = ProjectionEmitter.Emit(nestedSwiftFlatDtoGenerated, new ProjectionOptions(EmitSwiftFlatCodecs: true));
}
catch (InvalidDataException exception) when (
    exception.Message.Contains("dispatch-receipt.TrackingCode", StringComparison.Ordinal) &&
    exception.Message.Contains("nested DTOs", StringComparison.Ordinal))
{
    nestedSwiftFlatDtoRejected = true;
}
Require(nestedSwiftFlatDtoRejected,
    "Swift flat DTO codec emission silently omitted a nested DTO field.");

var zeroFieldIdGenerated = ReplaceCanonicalSchema(swiftFlatCodecGenerated, schema =>
{
    var receipt = schema["dtos"]!.AsArray()
        .Single(dto => dto!["id"]!.GetValue<int>() == 62)!;
    receipt["fields"]!.AsArray()[0]!["id"] = 0;
});
RequireSwiftFlatCodecRejected(
    zeroFieldIdGenerated,
    "uint16 field IDs",
    "Zero Swift flat DTO field ID was not rejected.");

var duplicateFieldIdGenerated = ReplaceCanonicalSchema(swiftFlatCodecGenerated, schema =>
{
    var receipt = schema["dtos"]!.AsArray()
        .Single(dto => dto!["id"]!.GetValue<int>() == 62)!;
    receipt["fields"]!.AsArray()[1]!["id"] = 1;
});
RequireSwiftFlatCodecRejected(
    duplicateFieldIdGenerated,
    "uint16 field IDs",
    "Duplicate Swift flat DTO field ID was not rejected.");

var nonascendingFieldIdGenerated = ReplaceCanonicalSchema(swiftFlatCodecGenerated, schema =>
{
    var receipt = schema["dtos"]!.AsArray()
        .Single(dto => dto!["id"]!.GetValue<int>() == 62)!;
    var fields = receipt["fields"]!.AsArray();
    var first = fields[0]!.DeepClone();
    var second = fields[1]!.DeepClone();
    fields[0] = second;
    fields[1] = first;
});
RequireSwiftFlatCodecRejected(
    nonascendingFieldIdGenerated,
    "uint16 field IDs",
    "Nonascending Swift flat DTO field IDs were not rejected.");

var streamSource = """
    #nullable enable
    using System;
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using NetLift;

    [LiftDto("pulse", Id = 21, Version = 1)]
    public sealed record Pulse([property: LiftField(1)] int Value);

    [LiftService("feed", Id = 20, Version = 1)]
    public interface IFeed
    {
        [LiftStream(4)]
        IAsyncEnumerable<Pulse> Watch(CancellationToken cancellationToken);

        [LiftStream(6)]
        IAsyncEnumerable<int> Numbers(CancellationToken cancellationToken);

        [LiftStream(7)]
        IAsyncEnumerable<bool> Flags(CancellationToken cancellationToken);

        [LiftStream(8)]
        IAsyncEnumerable<byte> Bytes(CancellationToken cancellationToken);

        [LiftStream(9)]
        IAsyncEnumerable<sbyte> SignedBytes(CancellationToken cancellationToken);

        [LiftStream(10)]
        IAsyncEnumerable<short> ShortValues(CancellationToken cancellationToken);

        [LiftStream(11)]
        IAsyncEnumerable<ushort> UnsignedShortValues(CancellationToken cancellationToken);

        [LiftStream(12)]
        IAsyncEnumerable<uint> UnsignedNumbers(CancellationToken cancellationToken);

        [LiftStream(13)]
        IAsyncEnumerable<long> LongNumbers(CancellationToken cancellationToken);

        [LiftStream(14)]
        IAsyncEnumerable<ulong> UnsignedLongNumbers(CancellationToken cancellationToken);

        [LiftStream(15)]
        IAsyncEnumerable<float> FloatValues(CancellationToken cancellationToken);

        [LiftStream(16)]
        IAsyncEnumerable<double> DoubleValues(CancellationToken cancellationToken);

        [LiftStream(17)]
        IAsyncEnumerable<string> Labels(CancellationToken cancellationToken);

        [LiftStream(18)]
        IAsyncEnumerable<string?> OptionalLabels(CancellationToken cancellationToken);

        [LiftEvent(5)]
        event Action<Pulse>? Changed;
    }
    """;
var (streamGenerated, streamCompilation, streamDiagnostics) = Generate(streamSource);
var (streamGeneratedAgain, _, _) = Generate(streamSource);
Require(streamGenerated == streamGeneratedAgain &&
    !streamDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) &&
    !streamCompilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
    "Valid stream/event contract was nondeterministic or failed to compile: " +
    string.Join(Environment.NewLine, streamDiagnostics.Concat(streamCompilation.GetDiagnostics())
        .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
using var streamJson = JsonDocument.Parse(ReadSchema(streamGenerated));
var streamRoot = streamJson.RootElement;
var streamHash = streamRoot.GetProperty("schemaHash").GetString()!;
var streamHashInput = ReadSchema(streamGenerated)
    .Replace($"\"schemaHash\":\"{streamHash}\"", "\"schemaHash\":null", StringComparison.Ordinal);
var schemaSubscriptions = streamRoot.GetProperty("services")[0].GetProperty("subscriptions")
    .EnumerateArray().ToArray();
var expectedScalarKinds = new Dictionary<int, string>
{
    [6] = "int32", [7] = "bool", [8] = "uint8", [9] = "int8",
    [10] = "int16", [11] = "uint16", [12] = "uint32", [13] = "int64",
    [14] = "uint64", [15] = "float32", [16] = "float64", [17] = "string",
    [18] = "string"
};
Require(streamRoot.GetProperty("schemaVersion").GetInt32() == 4 &&
    schemaSubscriptions.Length == 15 &&
    schemaSubscriptions.Any(subscription =>
        subscription.GetProperty("id").GetInt32() == 4 &&
        subscription.GetProperty("kind").GetString() == "stream" &&
        subscription.GetProperty("itemType").GetProperty("typeId").GetInt32() == 21) &&
    schemaSubscriptions.Any(subscription =>
        subscription.GetProperty("id").GetInt32() == 5 &&
        subscription.GetProperty("kind").GetString() == "event" &&
        subscription.GetProperty("itemType").GetProperty("typeId").GetInt32() == 21) &&
    expectedScalarKinds.All(expected => schemaSubscriptions.Any(subscription =>
        subscription.GetProperty("id").GetInt32() == expected.Key &&
        subscription.GetProperty("itemType").GetProperty("kind").GetString() == expected.Value)) &&
    schemaSubscriptions.Single(subscription => subscription.GetProperty("id").GetInt32() == 18)
        .GetProperty("itemType").GetProperty("nullable").GetBoolean() &&
    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(streamHashInput))).ToLowerInvariant() == streamHash,
    "Schema v4 did not encode stable subscription IDs, item types, or its canonical hash.");
Require(streamGenerated.Contains("DispatchStream_20_4", StringComparison.Ordinal) &&
    streamGenerated.Contains("AddEvent_20_5", StringComparison.Ordinal) &&
    streamGenerated.Contains("RemoveEvent_20_5", StringComparison.Ordinal),
    "Generated stream/event dispatch adapters are missing.");
var unsupportedStreamSource = streamSource.Replace(
    "IAsyncEnumerable<Pulse> Watch(CancellationToken cancellationToken);",
    "IAsyncEnumerable<System.DateTime> Watch(CancellationToken cancellationToken);",
    StringComparison.Ordinal);
var (_, _, unsupportedStreamDiagnostics) = Generate(unsupportedStreamSource);
Require(unsupportedStreamDiagnostics.Any(diagnostic =>
        diagnostic.Id == "NLT001" && diagnostic.GetMessage().Contains("DateTime", StringComparison.Ordinal)),
    "Unsupported stream item type did not produce a compiler diagnostic.");

using var assemblyStream = new MemoryStream();
var emit = outputCompilation.Emit(assemblyStream);
Require(emit.Success, "Generated compilation could not be emitted: " +
    string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
assemblyStream.Position = 0;
var fixtureAssembly = AssemblyLoadContext.Default.LoadFromStream(assemblyStream);
var personType = fixtureAssembly.GetType("Person", throwOnError: true)!;
var serviceType = fixtureAssembly.GetType("EchoService", throwOnError: true)!;
var service = Activator.CreateInstance(serviceType)!;
var person = Activator.CreateInstance(personType, "Ada", 36)!;
var dispatchType = fixtureAssembly.GetType("NetLift.Generated.Dispatch", throwOnError: true)!;
var dispatched = dispatchType.GetMethod("Dispatch_1_7")!.Invoke(null, [service, person])!;
Require((int)personType.GetProperty("Age")!.GetValue(dispatched)! == 37,
    "Generated direct-call dispatch did not call the service.");
var orderDraftType = fixtureAssembly.GetType("OrderDraft", throwOnError: true)!;
var orderDraft = Activator.CreateInstance(orderDraftType, "SKU-41", 3)!;
var asyncDispatchedTask = (Task)dispatchType.GetMethod("Dispatch_1_11")!
    .Invoke(null, [service, orderDraft, CancellationToken.None])!;
asyncDispatchedTask.GetAwaiter().GetResult();
var asyncDispatched = asyncDispatchedTask.GetType().GetProperty("Result")!.GetValue(asyncDispatchedTask)!;
Require((long)asyncDispatched.GetType().GetProperty("Id")!.GetValue(asyncDispatched)! == 42 &&
    (string)asyncDispatched.GetType().GetProperty("Sku")!.GetValue(asyncDispatched)! == "SKU-41" &&
    (int)asyncDispatched.GetType().GetProperty("Quantity")!.GetValue(asyncDispatched)! == 3,
    "Generated Task<T> direct-call dispatch did not call the service.");

var codecType = fixtureAssembly.GetType("NetLift.Generated.Codec", throwOnError: true)!;
var encodeDraft = codecType.GetMethod("TryEncode_Dto_3")!;
var draftEncodeArguments = new object?[] { orderDraft, null };
Require(encodeDraft.Invoke(null, draftEncodeArguments)!.ToString() == "Success",
    "Generated OrderDraft encoder rejected a valid DTO.");
var encodedDraft = (byte[])draftEncodeArguments[1]!;
Require(Convert.ToHexString(encodedDraft) == "010006000000534B552D343102000400000003000000",
    "Generated OrderDraft bytes did not match the canonical field-ID/length encoding.");
var repeatedDraftArguments = new object?[] { orderDraft, null };
Require(encodeDraft.Invoke(null, repeatedDraftArguments)!.ToString() == "Success" &&
    encodedDraft.SequenceEqual((byte[])repeatedDraftArguments[1]!),
    "Generated OrderDraft encoding was not deterministic.");

var decodeDraft = codecType.GetMethod("TryDecode_Dto_3")!;
var draftDecodeArguments = new object?[] { encodedDraft, null };
Require(decodeDraft.Invoke(null, draftDecodeArguments)!.ToString() == "Success",
    "Generated OrderDraft decoder rejected its encoded DTO.");
var decodedDraft = draftDecodeArguments[1]!;
Require((string)orderDraftType.GetProperty("Sku")!.GetValue(decodedDraft)! == "SKU-41" &&
    (int)orderDraftType.GetProperty("Quantity")!.GetValue(decodedDraft)! == 3,
    "Generated OrderDraft codec did not round-trip fields.");

var orderReceiptType = fixtureAssembly.GetType("OrderReceipt", throwOnError: true)!;
var orderReceipt = Activator.CreateInstance(orderReceiptType, 42L, "SKU-41", 3)!;
var encodeReceipt = codecType.GetMethod("TryEncode_Dto_4")!;
var receiptEncodeArguments = new object?[] { orderReceipt, null };
Require(encodeReceipt.Invoke(null, receiptEncodeArguments)!.ToString() == "Success",
    "Generated OrderReceipt encoder rejected a valid DTO.");
var encodedReceipt = (byte[])receiptEncodeArguments[1]!;
Require(Convert.ToHexString(encodedReceipt) ==
    "0100080000002A00000000000000020006000000534B552D343103000400000003000000",
    "Generated OrderReceipt bytes did not match the canonical field-ID/length encoding.");
var decodeReceipt = codecType.GetMethod("TryDecode_Dto_4")!;
var receiptDecodeArguments = new object?[] { encodedReceipt, null };
Require(decodeReceipt.Invoke(null, receiptDecodeArguments)!.ToString() == "Success",
    "Generated OrderReceipt decoder rejected its encoded DTO.");
var decodedReceipt = receiptDecodeArguments[1]!;
Require((long)orderReceiptType.GetProperty("Id")!.GetValue(decodedReceipt)! == 42 &&
    (string)orderReceiptType.GetProperty("Sku")!.GetValue(decodedReceipt)! == "SKU-41" &&
    (int)orderReceiptType.GetProperty("Quantity")!.GetValue(decodedReceipt)! == 3,
    "Generated OrderReceipt codec did not round-trip fields.");

var scalarTypesType = fixtureAssembly.GetType("ScalarTypes", throwOnError: true)!;
var scalarTypes = Activator.CreateInstance(scalarTypesType,
    new object?[] { true, (byte)250, (sbyte)-12, (short)-1234, (ushort)54321, -1234567, 3456789012u,
        -9000000000000L, 18000000000000UL, 1.5f, -2.25d, null })!;
var scalarEncodeArguments = new object?[] { scalarTypes, null };
Require(codecType.GetMethod("TryEncode_Dto_5")!.Invoke(null, scalarEncodeArguments)!.ToString() == "Success",
    "Generated codec rejected supported fixed-width scalar fields.");
var scalarDecodeArguments = new object?[] { (byte[])scalarEncodeArguments[1]!, null };
Require(codecType.GetMethod("TryDecode_Dto_5")!.Invoke(null, scalarDecodeArguments)!.ToString() == "Success",
    "Generated codec did not decode supported fixed-width scalar fields.");
var decodedScalars = scalarDecodeArguments[1]!;
Require((bool)scalarTypesType.GetProperty("Flag")!.GetValue(decodedScalars)! &&
    (byte)scalarTypesType.GetProperty("Unsigned8")!.GetValue(decodedScalars)! == 250 &&
    (sbyte)scalarTypesType.GetProperty("Signed8")!.GetValue(decodedScalars)! == -12 &&
    (short)scalarTypesType.GetProperty("Signed16")!.GetValue(decodedScalars)! == -1234 &&
    (ushort)scalarTypesType.GetProperty("Unsigned16")!.GetValue(decodedScalars)! == 54321 &&
    (int)scalarTypesType.GetProperty("Signed32")!.GetValue(decodedScalars)! == -1234567 &&
    (uint)scalarTypesType.GetProperty("Unsigned32")!.GetValue(decodedScalars)! == 3456789012u &&
    (long)scalarTypesType.GetProperty("Signed64")!.GetValue(decodedScalars)! == -9000000000000L &&
    (ulong)scalarTypesType.GetProperty("Unsigned64")!.GetValue(decodedScalars)! == 18000000000000UL &&
    (float)scalarTypesType.GetProperty("Float32")!.GetValue(decodedScalars)! == 1.5f &&
    (double)scalarTypesType.GetProperty("Float64")!.GetValue(decodedScalars)! == -2.25d &&
    scalarTypesType.GetProperty("Optional")!.GetValue(decodedScalars) is null,
    "Generated codec did not round-trip fixed-width values or nullable string.");

var nativeAdaptersType = fixtureAssembly.GetType("NetLift.Generated.NativeServiceAdapters", throwOnError: true)!;
var invokeConfirmation = nativeAdaptersType.GetMethod("Invoke_3_5")!;
uint invokedServiceId = 0;
uint invokedMethodId = 0;
byte[]? invokedRequest = null;
using var expectedCancellationSource = new CancellationTokenSource();
var expectedCancellation = expectedCancellationSource.Token;
var invokerCallCount = 0;
Func<uint, uint, byte[], CancellationToken, Task<bool>> nativeInvoker =
    (serviceId, methodId, request, cancellationToken) =>
    {
        invokedServiceId = serviceId;
        invokedMethodId = methodId;
        invokedRequest = request;
        invokerCallCount++;
        Require(cancellationToken == expectedCancellation,
            "Generated native adapter did not forward cancellation.");
        return Task.FromResult(false);
    };
var confirmationTask = (Task<bool>)invokeConfirmation.Invoke(null,
    [nativeInvoker, "Confirm order", expectedCancellation])!;
Require(!confirmationTask.GetAwaiter().GetResult() && invokedServiceId == 3 && invokedMethodId == 5 &&
    Convert.ToHexString(invokedRequest!) == "01000D000000436F6E6669726D206F72646572",
    "Generated native adapter did not route IDs, encode its request, and return typed bool.");
var invalidPromptTask = (Task<bool>)invokeConfirmation.Invoke(null,
    [nativeInvoker, "\uD800", expectedCancellation])!;
var rejectedInvalidPrompt = false;
try
{
    invalidPromptTask.GetAwaiter().GetResult();
}
catch (Exception exception) when (exception.GetType().Name == "CodecException")
{
    rejectedInvalidPrompt = exception.GetType().GetProperty("Status")!.GetValue(exception)!.ToString() == "InvalidValue";
}
Require(rejectedInvalidPrompt && invokerCallCount == 1,
    "Generated native adapter did not fail invalid UTF-16 before dispatch with an explicit codec status.");

var invalidUtf8 = (byte[])encodedDraft.Clone();
invalidUtf8[6] = 0xff;
Require(decodeDraft.Invoke(null, [invalidUtf8, null])!.ToString() == "Malformed",
    "Generated decoder accepted malformed UTF-8.");
var invalidFieldId = (byte[])encodedDraft.Clone();
invalidFieldId[0] = 9;
Require(decodeDraft.Invoke(null, [invalidFieldId, null])!.ToString() == "Malformed",
    "Generated decoder accepted an unexpected field ID.");
var invalidLength = (byte[])encodedDraft.Clone();
BinaryPrimitives.WriteUInt32LittleEndian(invalidLength.AsSpan(2, 4), uint.MaxValue);
Require(decodeDraft.Invoke(null, [invalidLength, null])!.ToString() == "Malformed",
    "Generated decoder accepted an invalid null sentinel for a non-null field.");
var overflowingLength = (byte[])encodedDraft.Clone();
BinaryPrimitives.WriteUInt32LittleEndian(overflowingLength.AsSpan(2, 4), uint.MaxValue - 1);
Require(decodeDraft.Invoke(null, [overflowingLength, null])!.ToString() == "Malformed",
    "Generated decoder did not reject an overflowing declared field length.");
Require(decodeDraft.Invoke(null, [encodedDraft.Append((byte)0).ToArray(), null])!.ToString() == "Malformed",
    "Generated decoder accepted trailing bytes.");
Require(decodeDraft.Invoke(null, [new byte[4097], null])!.ToString() == "BufferTooLarge",
    "Generated decoder did not reject input beyond its bound.");
var oversizeDraft = Activator.CreateInstance(orderDraftType, new string('x', 4096), 3)!;
var oversizeEncodeArguments = new object?[] { oversizeDraft, null };
Require(encodeDraft.Invoke(null, oversizeEncodeArguments)!.ToString() == "BufferTooLarge" &&
    ((byte[])oversizeEncodeArguments[1]!).Length == 0,
    "Generated encoder did not reject output beyond its bound.");
var invalidUtf16Draft = Activator.CreateInstance(orderDraftType, "\uD800", 3)!;
Require(encodeDraft.Invoke(null, [invalidUtf16Draft, null])!.ToString() == "InvalidValue",
    "Generated encoder replaced invalid UTF-16 instead of reporting it.");
var nullSkuDraft = Activator.CreateInstance(orderDraftType, new object?[] { null, 3 })!;
Require(encodeDraft.Invoke(null, [nullSkuDraft, null])!.ToString() == "InvalidValue",
    "Generated encoder accepted null for a non-null DTO field.");

var unsupportedSource = """
    using NetLift;
    [LiftService("bad", Id = 3)]
    public interface IBadService
    {
        [LiftMethod(1)]
        object Run(object input);

        [LiftMethod(2)]
        Task<object> RunAsync(CancellationToken cancellationToken);
    }
    [LiftDto("wide-field-id", Id = 4)]
    public sealed record WideFieldId([property: LiftField(65536)] int Value);
    [LiftNativeService("unsupported-native", Id = 6)]
    public interface IUnsupportedNative
    {
        [LiftMethod(1)]
        System.Threading.Tasks.Task<int> ReadAsync(int value);
    }
    """;
var (_, _, unsupportedDiagnostics) = Generate(unsupportedSource);
Require(unsupportedDiagnostics.Any(diagnostic => diagnostic.Id == "NLT001" &&
    diagnostic.GetMessage().Contains("object", StringComparison.Ordinal)),
    "Unsupported object signature did not produce a diagnostic: " +
    string.Join("; ", unsupportedDiagnostics.Select(diagnostic => $"{diagnostic.Id}: {diagnostic.GetMessage()}")));
Require(unsupportedDiagnostics.Any(diagnostic => diagnostic.Id == "NLT002" &&
    diagnostic.GetMessage().Contains("wire codec limit", StringComparison.Ordinal)),
    "Field ID outside the codec's uint16 range did not produce a diagnostic.");
Require(unsupportedDiagnostics.Any(diagnostic => diagnostic.Id == "NLT002" &&
    diagnostic.GetMessage().Contains("Generated native-service adapters", StringComparison.Ordinal)),
    "Unsupported native-service adapter signature did not produce a diagnostic.");

var projections = ProjectionEmitter.Emit(generated);
Require(Fingerprint(projections["netlift.h"]) == "cede85f24fe70f61fc077a7187291efb4cca26cc4f8fd485741c4801e536608c" &&
    Fingerprint(projections["NetLift.swift"]) == "64e57e7ccfbc01433300fc7db37015e8e471b1476b7228fb42162b3f117f5f56" &&
    Fingerprint(projections["NetLift.kt"]) == "8cbd40dc9434369888432f579f762793d48260cfe85084452140781eb71f22d4",
    "Schema v2 C/Swift/Kotlin projections changed byte-for-byte.");
var streamProjections = ProjectionEmitter.Emit(streamGenerated);
var streamProjectionOutput = Path.Combine(AppContext.BaseDirectory, "projection-stream-v4");
Directory.CreateDirectory(streamProjectionOutput);
foreach (var output in streamProjections)
    File.WriteAllText(Path.Combine(streamProjectionOutput, output.Key), output.Value);
Require(streamProjections["netlift.h"].Contains("LIFT_FEATURE_STREAMS 0x00000008u", StringComparison.Ordinal) &&
    streamProjections["netlift.h"].Contains("lift_stream_function_table", StringComparison.Ordinal) &&
    streamProjections["netlift.h"].Contains("LIFT_SUBSCRIPTION_ID_20_4 4", StringComparison.Ordinal) &&
    streamProjections["netlift.h"].Contains("LIFT_SUBSCRIPTION_ID_20_6 6", StringComparison.Ordinal) &&
    streamProjections["netlift.h"].Contains("LIFT_STATUS_STREAM_OVERFLOW = 14", StringComparison.Ordinal) &&
    streamProjections["NetLift.swift"].Contains("AsyncSequence", StringComparison.Ordinal) &&
    streamProjections["NetLift.swift"].Contains("liftStream_20_4", StringComparison.Ordinal) &&
    streamProjections["NetLift.swift"].Contains("liftStream_20_4(using abi: any LiftStreamABI) -> LiftStreamSequence<Pulse>", StringComparison.Ordinal) &&
    streamProjections["NetLift.swift"].Contains("liftStream_20_6(using abi: any LiftStreamABI) -> LiftStreamSequence<Int32>", StringComparison.Ordinal) &&
    streamProjections["NetLift.swift"].Contains("decodeDto_21", StringComparison.Ordinal) &&
    streamProjections["NetLift.kt"].Contains("Flow<", StringComparison.Ordinal) &&
    streamProjections["NetLift.kt"].Contains("watchStream", StringComparison.Ordinal) &&
    streamProjections["NetLift.kt"].Contains("watchStream(abi: LiftNativeStreamAbi): Flow<Pulse>", StringComparison.Ordinal) &&
    streamProjections["NetLift.kt"].Contains("numbersStream(abi: LiftNativeStreamAbi): Flow<Int>", StringComparison.Ordinal) &&
    streamProjections["NetLift.kt"].Contains("decodeDto_21", StringComparison.Ordinal) &&
    ProjectionEmitter.Emit(streamGenerated).SequenceEqual(streamProjections) &&
    Fingerprint(streamProjections["NetLift.swift"]) ==
        "38e4740683d324f9aaebead4e9c79f38e7badf1593ac2e870c5295a044395960" &&
    Fingerprint(streamProjections["NetLift.kt"]) ==
        "1c04b0f2a7ff8eb261ba41f81704fcf7c7e78133f25fcd5a2d02aff85536435f",
    "Schema v4 stream projections omitted the transport or cold facade surfaces.");
Require(projections.Count == 3 &&
    projections["netlift.h"].Contains("LIFT_METHOD_ID_1_7 7", StringComparison.Ordinal) &&
    projections["netlift.h"].Contains("LIFT_SERVICE_KIND_3_NATIVE 1", StringComparison.Ordinal) &&
    projections["netlift.h"].Contains("LIFT_METHOD_ASYNC_1_11 1", StringComparison.Ordinal) &&
    projections["netlift.h"].Contains("LIFT_METHOD_CANCELLABLE_3_5 1", StringComparison.Ordinal) &&
    projections["NetLift.swift"].Contains("protocol Echo", StringComparison.Ordinal) &&
    projections["NetLift.swift"].Contains("func placeAsync(input: OrderDraft) async throws -> OrderReceipt", StringComparison.Ordinal) &&
    projections["NetLift.swift"].Contains("func confirmAsync(prompt: String) async throws -> Bool", StringComparison.Ordinal) &&
    projections["NetLift.swift"].Contains("struct Person", StringComparison.Ordinal) &&
    projections["NetLift.kt"].Contains("interface Echo", StringComparison.Ordinal) &&
    projections["NetLift.kt"].Contains("suspend fun placeAsync(input: OrderDraft): OrderReceipt", StringComparison.Ordinal) &&
    projections["NetLift.kt"].Contains("suspend fun confirmAsync(prompt: String): Boolean", StringComparison.Ordinal) &&
    projections["NetLift.kt"].Contains("data class Person", StringComparison.Ordinal) &&
    projections["NetLift.kt"].Contains("package com.netlift.androidhost.contract", StringComparison.Ordinal) &&
    projections["NetLift.kt"].Contains("public suspend fun placeAsync(draft: OrderDraft): OrderReceipt", StringComparison.Ordinal) &&
    projections["NetLift.kt"].Contains("public interface LiftNativeAbi", StringComparison.Ordinal) &&
    projections["NetLift.kt"].Contains("schemaHash: ByteArray", StringComparison.Ordinal) &&
    projections["NetLift.kt"].Contains($"SCHEMA_HASH_HEX = \"{schemaHash}\"", StringComparison.Ordinal) &&
    projections["NetLift.kt"].Contains("fun onRequest(registrationToken: Long, requestToken: Long, methodId: Int, payload: ByteArray)", StringComparison.Ordinal) &&
    !projections["NetLift.kt"].Contains("NewStringUTF", StringComparison.Ordinal),
    "C/Swift/Kotlin async projections were not generated from the canonical schema.");
var orderProjectionOutput = Path.Combine(AppContext.BaseDirectory, "projection-orders");
Directory.CreateDirectory(orderProjectionOutput);
File.WriteAllText(Path.Combine(orderProjectionOutput, "NetLift.kt"), projections["NetLift.kt"]);
var corruptSchema = generated.Replace(schemaHash, new string('0', 64), StringComparison.Ordinal);
try
{
    ProjectionEmitter.Emit(corruptSchema);
    throw new InvalidOperationException("Projection accepted a schema with an invalid hash.");
}
catch (InvalidDataException)
{
}

var nativeNameCases = """
    using NetLift;
    using System.Threading;
    using System.Threading.Tasks;
    [LiftDto("lift-transport", Id = 4)]
    public sealed record @when(
        [property: LiftField(1)] string @class,
        [property: LiftField(2)] string Foo,
        [property: LiftField(3)] string foo);

    [LiftService("lift_transport", Id = 5)]
    public interface IKeywordService
    {
        [LiftMethod(1)]
        @when @class(string @when, string @switch);

        [LiftMethod(2)]
        @when Foo();

        [LiftMethod(3)]
        @when foo();

        [LiftMethod(4)]
        Task<@when> FooAsync(CancellationToken cancellationToken);
    }

    [LiftNativeService("native-confirm", Id = 6)]
    public interface IKeywordNative
    {
        [LiftMethod(1)]
        Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken);
    }
    """;
var (edgeGenerated, edgeCompilation, edgeDiagnostics) = Generate(nativeNameCases);
Require(!edgeDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) &&
        !edgeCompilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
    "Keyword/collision fixture did not compile through generation: " +
    string.Join("; ", edgeDiagnostics.Concat(edgeCompilation.GetDiagnostics())
        .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
        .Select(diagnostic => $"{diagnostic.Id}: {diagnostic.GetMessage()}")));
var edgeProjections = ProjectionEmitter.Emit(edgeGenerated);
Require(edgeProjections["NetLift.swift"].Contains("func `class`(when: String, `switch`: String)", StringComparison.Ordinal) &&
        edgeProjections["NetLift.kt"].Contains("fun `class`(`when`: String, switch: String)", StringComparison.Ordinal),
    "Swift/Kotlin keywords were not escaped: Swift=" + edgeProjections["NetLift.swift"] +
    " Kotlin=" + edgeProjections["NetLift.kt"]);
Require(edgeProjections["NetLift.swift"].Contains("LiftTransport_service_5", StringComparison.Ordinal) &&
        edgeProjections["NetLift.swift"].Contains("LiftTransport_dto_4", StringComparison.Ordinal) &&
        edgeProjections["NetLift.kt"].Contains("LiftTransport_service_5", StringComparison.Ordinal) &&
        edgeProjections["NetLift.kt"].Contains("LiftTransport_dto_4", StringComparison.Ordinal),
    "Projected type names did not avoid generated transport-name collisions.");
Require(edgeProjections["NetLift.swift"].Contains("foo_field_3", StringComparison.Ordinal) &&
        edgeProjections["NetLift.kt"].Contains("foo_field_3", StringComparison.Ordinal) &&
        edgeProjections["NetLift.swift"].Contains("foo_method_3", StringComparison.Ordinal) &&
        edgeProjections["NetLift.kt"].Contains("foo_method_3", StringComparison.Ordinal),
    "Projected field/method name collisions were not disambiguated.");
Require(edgeProjections["NetLift.swift"].Contains("func fooAsync() async throws -> LiftTransport_dto_4", StringComparison.Ordinal) &&
        edgeProjections["NetLift.swift"].Contains("func confirmAsync(prompt: String) async throws -> Bool", StringComparison.Ordinal) &&
        edgeProjections["NetLift.kt"].Contains("suspend fun fooAsync(): LiftTransport_dto_4", StringComparison.Ordinal) &&
        edgeProjections["NetLift.kt"].Contains("suspend fun confirmAsync(prompt: String): Boolean", StringComparison.Ordinal) &&
        edgeProjections["netlift.h"].Contains("LIFT_NATIVE_SERVICE_ID_6 6", StringComparison.Ordinal),
    "Async/native projections were missing or nondeterministic: Swift=" + edgeProjections["NetLift.swift"] +
    " Kotlin=" + edgeProjections["NetLift.kt"] + " C=" + edgeProjections["netlift.h"]);
var edgeOutput = Path.Combine(AppContext.BaseDirectory, "projection-edge-cases");
Directory.CreateDirectory(edgeOutput);
foreach (var output in edgeProjections)
    File.WriteAllText(Path.Combine(edgeOutput, output.Key), output.Value);

var v5OldSource = BuildV5Source(0, includeAdditions: false);
var v5NewSource = BuildV5Source(1, includeAdditions: true);
var (v5OldGenerated, v5OldCompilation, v5OldDiagnostics) = Generate(v5OldSource, "NetLiftV5Old");
var (v5NewGenerated, v5NewCompilation, v5NewDiagnostics) = Generate(v5NewSource, "NetLiftV5New");
var (v5OldGeneratedAgain, _, _) = Generate(v5OldSource, "NetLiftV5Old");
var (v5NewGeneratedAgain, _, _) = Generate(v5NewSource, "NetLiftV5New");
Require(v5OldGenerated.Length != 0 && v5NewGenerated.Length != 0 &&
        v5OldGenerated == v5OldGeneratedAgain && v5NewGenerated == v5NewGeneratedAgain &&
        !v5NewGenerated.Contains("public static class Codec", StringComparison.Ordinal) &&
        v5NewGenerated.Contains("Dispatch_17_1", StringComparison.Ordinal) &&
        v5NewGenerated.Contains("service.Submit(payload);", StringComparison.Ordinal) &&
        !v5OldDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) &&
        !v5NewDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) &&
        !v5OldCompilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) &&
        !v5NewCompilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
    "Valid schema-v5 generator fixtures were not deterministic or compilable.");

var v5OldManifestBytes = Encoding.UTF8.GetBytes(ReadSchema(v5OldGenerated));
var v5NewManifestBytes = Encoding.UTF8.GetBytes(ReadSchema(v5NewGenerated));
var v5OldManifest = NegotiationManifest.LoadEmbedded(v5OldManifestBytes);
var v5NewManifest = NegotiationManifest.LoadEmbedded(v5NewManifestBytes);
Require(v5OldManifest.SchemaHash != v5NewManifest.SchemaHash &&
        v5OldManifest.SchemaHash == "7daf912c2b104aec99e7ce9febc2e2294dba125856362fa866f9d35e16648184" &&
        v5NewManifest.SchemaHash == "9f1c59138984b5f5f37bf81cf9ae18650cee292813d8712b8ca4efce02ca4245" &&
        v5OldManifest.ContractMajor == 1 && v5OldManifest.ContractMinor == 0 &&
        v5NewManifest.ContractMajor == 1 && v5NewManifest.ContractMinor == 1 &&
        v5OldManifest.ServiceId == 17 && v5OldManifest.ServiceName == "Orders" &&
        v5OldManifest.MethodId == 1 && v5OldManifest.MethodName == "Submit" &&
        v5OldManifest.DtoId == 21 && v5OldManifest.DtoName == "Payload" &&
        v5OldManifest.FeatureBits == 0x10 && v5NewManifest.FeatureBits == 0x10 &&
        v5NewManifest.Fields.Select(field => field.Id).SequenceEqual(new ushort[] { 1, 2, 7, 8, 9 }) &&
        v5NewManifest.Fields.Count == 5 && v5NewManifest.Fields[2].Optional &&
        Equals(v5NewManifest.Fields[2].DefaultValue, "") &&
        v5NewManifest.Fields[3].Optional && v5NewManifest.Fields[3].Nullable &&
        v5NewManifest.Fields[3].DefaultValue is null &&
        v5NewManifest.Fields[4].Optional && Equals(v5NewManifest.Fields[4].DefaultValue, false),
    "Generator-owned schema-v5 manifest omitted its stable route/revision or optional defaults.");

var preStartRouteCalls = 0;
var oldStartEndpoint = new CompatibleStartReference(
    v5OldManifestBytes, localCompatibleStartOptIn: true,
    minimumPeerContractMajor: 1, minimumPeerContractMinor: 0);
ExpectNegotiationException(
    () => oldStartEndpoint.Dispatch([], _ => ++preStartRouteCalls),
    NegotiationErrorCode.NotStarted,
    "route call before compatible start");
Require(preStartRouteCalls == 0, "A route became callable before compatible-start.");

var oldCompatibleStart = oldStartEndpoint.Start(
    v5NewManifestBytes,
    CompatibleStartPeerHelloV1.Encode(v5NewManifest),
    new PeerManifestTrust(v5NewManifest.SchemaHash, 1, 0),
    peerCompatibleStartOptIn: true);
var newStartEndpoint = new CompatibleStartReference(
    v5NewManifestBytes, localCompatibleStartOptIn: true,
    minimumPeerContractMajor: 1, minimumPeerContractMinor: 0);
var newCompatibleStart = newStartEndpoint.Start(
    v5OldManifestBytes,
    CompatibleStartPeerHelloV1.Encode(v5OldManifest),
    new PeerManifestTrust(v5OldManifest.SchemaHash, 1, 0),
    peerCompatibleStartOptIn: true);
Require(oldCompatibleStart.Succeeded && newCompatibleStart.Succeeded &&
        oldCompatibleStart.Contract!.Mode == NegotiationMode.AdditiveOptionalFields &&
        newCompatibleStart.Contract!.Mode == NegotiationMode.AdditiveOptionalFields,
    "Versioned compatible-start ABI did not establish both generated old/new directions.");

var routeCalls = 0;
var newToOldCall = newCompatibleStart.Contract!.EncodeDto(new Dictionary<ushort, object?>
{
    [1] = "ABI-NEW",
    [2] = 11,
    [7] = "extra",
    [8] = null,
    [9] = true
});
var oldRouteValue = oldStartEndpoint.Dispatch(newToOldCall, fields =>
{
    routeCalls++;
    Require(fields.Count == 2 && Equals(fields[1], "ABI-NEW") && Equals(fields[2], 11),
        "Old route did not receive the decoded compatible-start DTO.");
    return (string)fields[1]!;
});
var oldToNewCall = oldCompatibleStart.Contract!.EncodeDto(new Dictionary<ushort, object?>
{
    [1] = "ABI-OLD",
    [2] = 12
});
var newRouteValue = newStartEndpoint.Dispatch(oldToNewCall, fields =>
{
    routeCalls++;
    Require(fields.Count == 5 && Equals(fields[7], "") && fields[8] is null &&
            Equals(fields[9], false),
        "New route did not receive materialized generated optional defaults.");
    return (string)fields[1]!;
});
Require(routeCalls == 2 && oldRouteValue == "ABI-NEW" && newRouteValue == "ABI-OLD",
    "Compatible-start routes did not execute only after negotiation in both directions.");

var routeCallsBeforeMalformedPayload = routeCalls;
ExpectNegotiationException(
    () => oldStartEndpoint.Dispatch([0x00, 0x01, 0x02], _ => ++routeCalls),
    NegotiationErrorCode.WireInvalid,
    "malformed DTO before managed route");
Require(routeCalls == routeCallsBeforeMalformedPayload,
    "Malformed negotiated DTO invoked the managed route.");

var duplicateStartResult = oldStartEndpoint.Start(
    v5OldManifestBytes,
    CompatibleStartPeerHelloV1.Encode(v5OldManifest),
    new PeerManifestTrust(v5OldManifest.SchemaHash, 1, 0),
    peerCompatibleStartOptIn: true);
Require(!duplicateStartResult.Succeeded &&
        duplicateStartResult.ErrorCode == NegotiationErrorCode.OptedOut,
    "A duplicate start after success did not reject as a one-shot endpoint.");
var retainedRouteResult = oldStartEndpoint.Dispatch(newToOldCall,
    fields => (string)fields[1]!);
Require(retainedRouteResult == "ABI-NEW",
    "A rejected duplicate start replaced the established negotiated route context.");

var failedStartEndpoint = new CompatibleStartReference(v5OldManifestBytes, true, 1, 0);
var failedInitialStart = failedStartEndpoint.Start(
    v5NewManifestBytes,
    CompatibleStartPeerHelloV1.Encode(v5NewManifest),
    new PeerManifestTrust(v5OldManifest.SchemaHash, 1, 0),
    peerCompatibleStartOptIn: true);
Require(!failedInitialStart.Succeeded &&
        failedInitialStart.ErrorCode == NegotiationErrorCode.UntrustedPeerManifest,
    "The fail-then-retry test did not begin with a rejected trust pin.");
var retryStartResult = failedStartEndpoint.Start(
    v5NewManifestBytes,
    CompatibleStartPeerHelloV1.Encode(v5NewManifest),
    new PeerManifestTrust(v5NewManifest.SchemaHash, 1, 0),
    peerCompatibleStartOptIn: true);
Require(!retryStartResult.Succeeded && retryStartResult.ErrorCode == NegotiationErrorCode.OptedOut,
    "A failed initial start was unexpectedly retryable.");
var failedRetryRouteCalls = 0;
ExpectNegotiationException(
    () => failedStartEndpoint.Dispatch(newToOldCall, _ => ++failedRetryRouteCalls),
    NegotiationErrorCode.NotStarted,
    "route call after failed start and rejected retry");
Require(failedRetryRouteCalls == 0,
    "A failed initial start or rejected retry exposed the managed route.");

var wrongHashEndpoint = new CompatibleStartReference(
    v5OldManifestBytes, true, 1, 0);
var wrongHashResult = wrongHashEndpoint.Start(
    v5NewManifestBytes,
    CompatibleStartPeerHelloV1.Encode(v5NewManifest),
    new PeerManifestTrust(v5OldManifest.SchemaHash, 1, 0),
    true);
Require(!wrongHashResult.Succeeded &&
        wrongHashResult.ErrorCode == NegotiationErrorCode.UntrustedPeerManifest,
    "Compatible-start accepted a peer manifest with the wrong trusted full-schema-hash pin.");

var wrongHelloHashEndpoint = new CompatibleStartReference(
    v5OldManifestBytes, true, 1, 0);
var wrongHelloHashResult = wrongHelloHashEndpoint.Start(
    v5NewManifestBytes,
    CompatibleStartPeerHelloV1.Encode(
        1, v5OldManifest.SchemaHash, v5NewManifest.ContractMajor,
        v5NewManifest.ContractMinor, v5NewManifest.FeatureBits),
    new PeerManifestTrust(v5NewManifest.SchemaHash, 1, 0),
    true);
Require(!wrongHelloHashResult.Succeeded &&
        wrongHelloHashResult.ErrorCode == NegotiationErrorCode.UntrustedPeerManifest,
    "Compatible-start accepted a peer hello with the wrong full-schema-hash claim.");

var staleRevisionEndpoint = new CompatibleStartReference(
    v5OldManifestBytes, true, 1, 0);
var staleRevisionResult = staleRevisionEndpoint.Start(
    v5NewManifestBytes,
    CompatibleStartPeerHelloV1.Encode(1, v5NewManifest.SchemaHash, 1, 0, v5NewManifest.FeatureBits),
    new PeerManifestTrust(v5NewManifest.SchemaHash, 1, 0),
    true);
Require(!staleRevisionResult.Succeeded &&
        staleRevisionResult.ErrorCode == NegotiationErrorCode.UntrustedPeerManifest,
    "Compatible-start accepted a stale peer revision claim.");

var revisionFloorEndpoint = new CompatibleStartReference(
    v5OldManifestBytes, true, 2, 0);
var revisionFloorResult = revisionFloorEndpoint.Start(
    v5NewManifestBytes,
    CompatibleStartPeerHelloV1.Encode(v5NewManifest),
    new PeerManifestTrust(v5NewManifest.SchemaHash, 1, 0),
    true);
Require(!revisionFloorResult.Succeeded &&
        revisionFloorResult.ErrorCode == NegotiationErrorCode.UntrustedPeerManifest,
    "Compatible-start accepted a trusted peer below the local revision floor.");

var unsupportedFeatureSchema = MutateManifest(v5NewManifestBytes,
    schema => schema["featureBits"] = 0x80000000);
var unsupportedFeatureEnvelope = JsonNode.Parse(unsupportedFeatureSchema)!;
var unsupportedFeatureHash = unsupportedFeatureEnvelope["schemaHash"]!.GetValue<string>();
var unsupportedFeatureEndpoint = new CompatibleStartReference(
    v5OldManifestBytes, true, 1, 0);
var unsupportedFeatureResult = unsupportedFeatureEndpoint.Start(
    unsupportedFeatureSchema,
    CompatibleStartPeerHelloV1.Encode(1, unsupportedFeatureHash, 1, 1, 0x80000000),
    new PeerManifestTrust(unsupportedFeatureHash, 1, 0),
    true);
Require(!unsupportedFeatureResult.Succeeded &&
        unsupportedFeatureResult.ErrorCode == NegotiationErrorCode.FeatureMismatch,
    "Compatible-start accepted an unsupported peer feature claim.");

var abiMismatchEndpoint = new CompatibleStartReference(
    v5OldManifestBytes, true, 1, 0);
var abiMismatchResult = abiMismatchEndpoint.Start(
    v5NewManifestBytes,
    CompatibleStartPeerHelloV1.Encode(2, v5NewManifest.SchemaHash, 1, 1, v5NewManifest.FeatureBits),
    new PeerManifestTrust(v5NewManifest.SchemaHash, 1, 0),
    true);
Require(!abiMismatchResult.Succeeded &&
        abiMismatchResult.ErrorCode == NegotiationErrorCode.VersionMismatch,
    "Compatible-start accepted a peer hello with an ABI-major mismatch.");

var invalidManifestEndpoint = new CompatibleStartReference(
    v5OldManifestBytes, true, 1, 0);
var invalidManifestResult = invalidManifestEndpoint.Start(
    v5NewManifestBytes[..^1],
    CompatibleStartPeerHelloV1.Encode(v5NewManifest),
    new PeerManifestTrust(v5NewManifest.SchemaHash, 1, 0),
    true);
Require(!invalidManifestResult.Succeeded &&
        invalidManifestResult.ErrorCode == NegotiationErrorCode.InvalidManifest,
    "Compatible-start accepted a truncated peer manifest.");
ExpectNegotiationException(
    () => invalidManifestEndpoint.Dispatch(newToOldCall, _ => ++preStartRouteCalls),
    NegotiationErrorCode.NotStarted,
    "route call after failed compatible start");
Require(preStartRouteCalls == 0, "A route became callable after a failed compatible-start.");

var truncatedHelloEndpoint = new CompatibleStartReference(
    v5OldManifestBytes, true, 1, 0);
var truncatedHelloResult = truncatedHelloEndpoint.Start(
    v5NewManifestBytes,
    CompatibleStartPeerHelloV1.Encode(v5NewManifest)[..^1],
    new PeerManifestTrust(v5NewManifest.SchemaHash, 1, 0),
    true);
Require(!truncatedHelloResult.Succeeded &&
        truncatedHelloResult.ErrorCode == NegotiationErrorCode.InvalidManifest,
    "Compatible-start accepted a truncated versioned peer hello.");

var peerOptOutEndpoint = new CompatibleStartReference(v5OldManifestBytes, true, 1, 0);
var peerOptOutResult = peerOptOutEndpoint.Start(
    v5NewManifestBytes,
    CompatibleStartPeerHelloV1.Encode(v5NewManifest),
    new PeerManifestTrust(v5NewManifest.SchemaHash, 1, 0),
    peerCompatibleStartOptIn: false);
Require(!peerOptOutResult.Succeeded && peerOptOutResult.ErrorCode == NegotiationErrorCode.OptedOut,
    "Compatible-start accepted a peer that did not opt in to additive negotiation.");

var v5ProjectionRejected = false;
try
{
    _ = ProjectionEmitter.Emit(v5NewGenerated);
}
catch (InvalidDataException exception) when (
    exception.Message.Contains("supported NetLift schema shape", StringComparison.Ordinal))
{
    v5ProjectionRejected = true;
}
Require(v5ProjectionRejected, "The v1-v4 native projection emitter unexpectedly accepted schema-v5.");
var v5OldToNew = NegotiationPreflight.Start(
    v5OldManifest,
    NegotiationManifest.LoadTrustedPeer(v5NewManifestBytes,
        new PeerManifestTrust(v5NewManifest.SchemaHash, 1, 0)),
    v5NewManifest.SchemaHash, v5NewManifest.FeatureBits,
    localCompatibleStartOptIn: true, peerCompatibleStartOptIn: true,
    minimumPeerContractMajor: 1, minimumPeerContractMinor: 0);
var v5NewToOld = NegotiationPreflight.Start(
    v5NewManifest,
    NegotiationManifest.LoadTrustedPeer(v5OldManifestBytes,
        new PeerManifestTrust(v5OldManifest.SchemaHash, 1, 0)),
    v5OldManifest.SchemaHash, v5OldManifest.FeatureBits,
    localCompatibleStartOptIn: true, peerCompatibleStartOptIn: true,
    minimumPeerContractMajor: 1, minimumPeerContractMinor: 0);
Require(v5OldToNew.Succeeded && v5NewToOld.Succeeded &&
        v5OldToNew.Contract!.Mode == NegotiationMode.AdditiveOptionalFields &&
        v5NewToOld.Contract!.Mode == NegotiationMode.AdditiveOptionalFields,
    "Generated old/new manifests did not pass trusted bilateral additive preflight.");

var v5OldCodecProjection = ProjectionEmitter.Emit(
    v5OldGenerated, new ProjectionOptions(EmitSchemaV5Codecs: true));
var v5NewCodecProjection = ProjectionEmitter.Emit(
    v5NewGenerated, new ProjectionOptions(EmitSchemaV5Codecs: true));
var v5NewCodecProjectionAgain = ProjectionEmitter.Emit(
    v5NewGenerated, new ProjectionOptions(EmitSchemaV5Codecs: true));
var v5OldHostCodecProjection = ProjectionEmitter.Emit(
    v5OldGenerated, new ProjectionOptions(EmitSchemaV5HostCodecs: true));
var v5NewHostCodecProjection = ProjectionEmitter.Emit(
    v5NewGenerated, new ProjectionOptions(EmitSchemaV5HostCodecs: true));
var v5NewHostCodecProjectionAgain = ProjectionEmitter.Emit(
    v5NewGenerated, new ProjectionOptions(EmitSchemaV5HostCodecs: true));
var expectedV5ProjectionFiles = new[] { "NetLift.kt", "NetLift.swift" };
Require(v5OldCodecProjection.Keys.OrderBy(key => key, StringComparer.Ordinal)
            .SequenceEqual(expectedV5ProjectionFiles.OrderBy(key => key, StringComparer.Ordinal)) &&
        v5NewCodecProjection.Keys.OrderBy(key => key, StringComparer.Ordinal)
            .SequenceEqual(expectedV5ProjectionFiles.OrderBy(key => key, StringComparer.Ordinal)) &&
        v5NewCodecProjection.SequenceEqual(v5NewCodecProjectionAgain) &&
        Fingerprint(v5OldCodecProjection["NetLift.swift"]) ==
            "cd6d08121e1ca496cc633fa1772b1616ff0580a3f95af954e830fb62b279db20" &&
        Fingerprint(v5OldCodecProjection["NetLift.kt"]) ==
            "80dd20dc130534275cd7829b910601f3db5ff44e8b3fce65da58e796ece85861" &&
        Fingerprint(v5NewCodecProjection["NetLift.swift"]) ==
            "712f5f9af1d31331499f6b4e8abb8865d53dd23d2ac4508eec702c928f517af8" &&
        Fingerprint(v5NewCodecProjection["NetLift.kt"]) ==
            "865abfdda32ee3fb4d7e53d9feb1de4b9da8c8bb18721de384ba34f74381bc1e" &&
        v5OldHostCodecProjection.Keys.OrderBy(key => key, StringComparer.Ordinal)
            .SequenceEqual(expectedV5ProjectionFiles.OrderBy(key => key, StringComparer.Ordinal)) &&
        v5NewHostCodecProjection.SequenceEqual(v5NewHostCodecProjectionAgain) &&
        Fingerprint(v5OldHostCodecProjection["NetLift.swift"]) ==
            "22f3a9a44bb3d3912f33f5dfc24c2fb5b9eb3286faf11bf21540debe9751e177" &&
        Fingerprint(v5OldHostCodecProjection["NetLift.kt"]) ==
            "521fb4bf22068e7322ad747c7377e09e5e304c95fb9e585b6c217cee4e2724c1" &&
        Fingerprint(v5NewHostCodecProjection["NetLift.swift"]) ==
            "113113ad607aab8cfbc4b41071f808d9a81dce8e403b89c9820bcaa27a22f6d0" &&
        Fingerprint(v5NewHostCodecProjection["NetLift.kt"]) ==
            "137584440646e49310a20ba48487453b1852a68e47e37e2e88c0a48221d7f687" &&
        v5OldHostCodecProjection["NetLift.swift"].Contains("import NetLiftV5Host", StringComparison.Ordinal) &&
        v5OldHostCodecProjection["NetLift.swift"].Contains("NetLiftV5PeerContext", StringComparison.Ordinal) &&
        v5NewHostCodecProjection["NetLift.kt"].Contains(
            "com.netlift.androidruntime.v5.NegotiatedPeerContext", StringComparison.Ordinal) &&
        v5NewHostCodecProjection["NetLift.kt"].Contains("context.requireActive()", StringComparison.Ordinal) &&
        v5NewHostCodecProjection["NetLift.kt"].Contains("context.serviceId != EXPECTED_SERVICE_ID", StringComparison.Ordinal) &&
        v5NewHostCodecProjection["NetLift.kt"].Contains("context.methodId != EXPECTED_METHOD_ID", StringComparison.Ordinal) &&
        v5NewHostCodecProjection["NetLift.kt"].Contains("context.dtoId != EXPECTED_DTO_ID", StringComparison.Ordinal) &&
        !v5NewHostCodecProjection["NetLift.kt"].Contains("context.serviceID", StringComparison.Ordinal) &&
        !v5NewHostCodecProjection["NetLift.kt"].Contains("context.methodID", StringComparison.Ordinal) &&
        !v5NewHostCodecProjection["NetLift.kt"].Contains("context.dtoID", StringComparison.Ordinal) &&
        !v5OldHostCodecProjection["NetLift.swift"].Contains("testingOnly", StringComparison.Ordinal) &&
        !v5OldHostCodecProjection["NetLift.kt"].Contains("testingOnly", StringComparison.Ordinal) &&
        !v5NewHostCodecProjection["NetLift.swift"].Contains("testingOnly", StringComparison.Ordinal) &&
        !v5NewHostCodecProjection["NetLift.kt"].Contains("testingOnly", StringComparison.Ordinal) &&
        !v5OldHostCodecProjection["NetLift.swift"].Contains("decodePayload", StringComparison.Ordinal) &&
        !v5OldHostCodecProjection["NetLift.kt"].Contains("decodePayload", StringComparison.Ordinal) &&
        !v5NewHostCodecProjection["NetLift.swift"].Contains("decodePayload", StringComparison.Ordinal) &&
        !v5NewHostCodecProjection["NetLift.kt"].Contains("decodePayload", StringComparison.Ordinal) &&
        v5OldHostCodecProjection["NetLift.swift"].Contains(
            "context.schemaHash == NetLiftV5DtoCodec.schemaHash", StringComparison.Ordinal) &&
        v5OldHostCodecProjection["NetLift.kt"].Contains(
            "context.schemaHashHex != schemaHash", StringComparison.Ordinal) &&
        v5OldCodecProjection["NetLift.swift"].Contains(v5OldManifest.SchemaHash, StringComparison.Ordinal) &&
        v5NewCodecProjection["NetLift.kt"].Contains(v5NewManifest.SchemaHash, StringComparison.Ordinal) &&
        v5NewCodecProjection["NetLift.swift"].Contains("canonicalJson", StringComparison.Ordinal) &&
        v5NewCodecProjection["NetLift.kt"].Contains("canonicalJson", StringComparison.Ordinal) &&
        !v5NewCodecProjection.ContainsKey("netlift.h"),
    "Schema-v5 opt-in projection was nondeterministic, omitted its exact manifest/hash, or emitted a C ABI header. " +
    $"Codec-only hashes: {Fingerprint(v5OldCodecProjection["NetLift.swift"])}, " +
    $"{Fingerprint(v5OldCodecProjection["NetLift.kt"])}, " +
    $"{Fingerprint(v5NewCodecProjection["NetLift.swift"])}, " +
    $"{Fingerprint(v5NewCodecProjection["NetLift.kt"])}. Host decode present: " +
    $"{v5OldHostCodecProjection["NetLift.swift"].Contains("decodePayload", StringComparison.Ordinal)}/" +
    $"{v5OldHostCodecProjection["NetLift.kt"].Contains("decodePayload", StringComparison.Ordinal)}.");

var v5HashTamperRejected = false;
try
{
    _ = ProjectionEmitter.Emit(
        v5NewGenerated.Replace(v5NewManifest.SchemaHash, new string('0', 64), StringComparison.Ordinal),
        new ProjectionOptions(EmitSchemaV5Codecs: true));
}
catch (InvalidDataException exception) when (
    exception.Message.Contains("hash does not match", StringComparison.Ordinal))
{
    v5HashTamperRejected = true;
}
Require(v5HashTamperRejected, "The schema-v5 projection accepted a manifest with a stale full-schema hash.");

var staleSchemaHashConstantSource = v5NewGenerated.Replace(
    $"public const string SchemaHash = \"{v5NewManifest.SchemaHash}\";",
    $"public const string SchemaHash = \"{new string('0', 64)}\";",
    StringComparison.Ordinal);
Require(staleSchemaHashConstantSource != v5NewGenerated &&
        ReadSchema(staleSchemaHashConstantSource) == ReadSchema(v5NewGenerated),
    "The stale-constant regression did not mutate only Contract.SchemaHash.");
var v5SchemaHashConstantTamperRejected = false;
try
{
    _ = ProjectionEmitter.Emit(
        staleSchemaHashConstantSource, new ProjectionOptions(EmitSchemaV5Codecs: true));
}
catch (InvalidDataException exception) when (
    exception.Message.Contains("Contract.SchemaHash constant", StringComparison.Ordinal))
{
    v5SchemaHashConstantTamperRejected = true;
}
Require(v5SchemaHashConstantTamperRejected,
    "The schema-v5 projection accepted a Contract.SchemaHash constant that disagrees with its manifest.");

var decoyCanonicalJsonSource = v5NewGenerated.Replace(
    "public static class Contract",
    $"public static class Decoy {{ public const string CanonicalJson = " +
    $"{System.Text.Json.JsonSerializer.Serialize(ReadSchema(v5OldGenerated))}; }}\n" +
    "public static class Contract",
    StringComparison.Ordinal).Replace(
    $"public const string SchemaHash = \"{v5NewManifest.SchemaHash}\";",
    $"public const string SchemaHash = \"{v5OldManifest.SchemaHash}\";",
    StringComparison.Ordinal);
Require(decoyCanonicalJsonSource != v5NewGenerated &&
        ReadSchema(decoyCanonicalJsonSource) == ReadSchema(v5OldGenerated),
    "The decoy-constant regression did not put the old schema before Contract.CanonicalJson.");
var v5DecoyCanonicalJsonRejected = false;
try
{
    _ = ProjectionEmitter.Emit(
        decoyCanonicalJsonSource, new ProjectionOptions(EmitSchemaV5Codecs: true));
}
catch (InvalidDataException exception) when (
    exception.Message.Contains("Contract.SchemaHash constant", StringComparison.Ordinal))
{
    v5DecoyCanonicalJsonRejected = true;
}
Require(v5DecoyCanonicalJsonRejected,
    "The schema-v5 projection read a decoy CanonicalJson outside the generated Contract class.");

var v1ToV4RejectedByV5OptIn = false;
try
{
    _ = ProjectionEmitter.Emit(
        flatCodecGenerated, new ProjectionOptions(EmitSchemaV5Codecs: true));
}
catch (InvalidDataException exception) when (
    exception.Message.Contains("missing or unsupported properties", StringComparison.Ordinal))
{
    v1ToV4RejectedByV5OptIn = true;
}
Require(v1ToV4RejectedByV5OptIn, "The schema-v5 projection accepted a v1-v4 schema.");

var v5NewPayload = v5NewToOld.Contract!.EncodeDto(new Dictionary<ushort, object?>
{
    [1] = "SKU-7",
    [2] = 3,
    [7] = "new note",
    [8] = null,
    [9] = true
});
var v5OldDecoded = v5OldToNew.Contract!.DecodeDto(v5NewPayload);
var v5OldPayload = v5OldToNew.Contract.EncodeDto(new Dictionary<ushort, object?>
{
    [1] = "SKU-8",
    [2] = 4
});
var v5NewDecoded = v5NewToOld.Contract.DecodeDto(v5OldPayload);
Require(v5OldDecoded.Count == 2 && Equals(v5OldDecoded[1], "SKU-7") &&
        Equals(v5OldDecoded[2], 3) && v5NewDecoded.Count == 5 &&
        Equals(v5NewDecoded[7], "") && v5NewDecoded[8] is null &&
        Equals(v5NewDecoded[9], false),
    "Generated old/new manifest wire proof did not skip or materialize declared optional defaults.");

var v5CodecOldPayload = v5OldToNew.Contract!.EncodeDto(new Dictionary<ushort, object?>
{
    [1] = "SKU-OLD",
    [2] = 19
});
var v5CodecNewPayload = v5NewToOld.Contract!.EncodeDto(new Dictionary<ushort, object?>
{
    [1] = "SKU-NEW",
    [2] = 42,
    [7] = "",
    [8] = null,
    [9] = false
});
var v5TrustedOldPeer = NegotiationManifest.LoadTrustedPeer(
    v5OldManifestBytes, new PeerManifestTrust(v5OldManifest.SchemaHash, 1, 0));
var v5TrustedNewPeer = NegotiationManifest.LoadTrustedPeer(
    v5NewManifestBytes, new PeerManifestTrust(v5NewManifest.SchemaHash, 1, 0));
var v5CodecOutput = Path.Combine(AppContext.BaseDirectory, "projection-schema-v5-codecs");
var v5CodecOldDirectory = Path.Combine(v5CodecOutput, "old");
var v5CodecNewDirectory = Path.Combine(v5CodecOutput, "new");
var v5CodecFixtureDirectory = Path.Combine(v5CodecOutput, "fixtures");
Directory.CreateDirectory(v5CodecOldDirectory);
Directory.CreateDirectory(v5CodecNewDirectory);
Directory.CreateDirectory(v5CodecFixtureDirectory);
File.WriteAllText(Path.Combine(v5CodecOldDirectory, "NetLift.swift"),
    v5OldCodecProjection["NetLift.swift"]);
File.WriteAllText(Path.Combine(v5CodecOldDirectory, "NetLift.kt"),
    v5OldCodecProjection["NetLift.kt"]);
File.WriteAllText(Path.Combine(v5CodecNewDirectory, "NetLift.swift"),
    v5NewCodecProjection["NetLift.swift"]);
File.WriteAllText(Path.Combine(v5CodecNewDirectory, "NetLift.kt"),
    v5NewCodecProjection["NetLift.kt"]);
File.WriteAllBytes(Path.Combine(v5CodecFixtureDirectory, "old-payload.bin"), v5CodecOldPayload);
File.WriteAllBytes(Path.Combine(v5CodecFixtureDirectory, "new-payload.bin"), v5CodecNewPayload);
WriteV5PeerSchemaFixture(v5TrustedOldPeer, Path.Combine(v5CodecFixtureDirectory, "peer-old.txt"));
WriteV5PeerSchemaFixture(v5TrustedNewPeer, Path.Combine(v5CodecFixtureDirectory, "peer-new.txt"));

var v5HostCodecOutput = Path.Combine(AppContext.BaseDirectory, "projection-schema-v5-host-codecs");
var v5HostCodecOldDirectory = Path.Combine(v5HostCodecOutput, "old");
var v5HostCodecNewDirectory = Path.Combine(v5HostCodecOutput, "new");
var v5HostCodecFixtureDirectory = Path.Combine(v5HostCodecOutput, "fixtures");
Directory.CreateDirectory(v5HostCodecOldDirectory);
Directory.CreateDirectory(v5HostCodecNewDirectory);
Directory.CreateDirectory(v5HostCodecFixtureDirectory);
File.WriteAllText(Path.Combine(v5HostCodecOldDirectory, "NetLift.swift"),
    v5OldHostCodecProjection["NetLift.swift"]);
File.WriteAllText(Path.Combine(v5HostCodecOldDirectory, "NetLift.kt"),
    v5OldHostCodecProjection["NetLift.kt"]);
File.WriteAllText(Path.Combine(v5HostCodecNewDirectory, "NetLift.swift"),
    v5NewHostCodecProjection["NetLift.swift"]);
File.WriteAllText(Path.Combine(v5HostCodecNewDirectory, "NetLift.kt"),
    v5NewHostCodecProjection["NetLift.kt"]);
File.WriteAllText(Path.Combine(v5HostCodecFixtureDirectory, "old-contract.g.cs"), v5OldGenerated);
File.WriteAllText(Path.Combine(v5HostCodecFixtureDirectory, "new-contract.g.cs"), v5NewGenerated);
File.WriteAllBytes(Path.Combine(v5HostCodecFixtureDirectory, "old-payload.bin"), v5CodecOldPayload);
File.WriteAllBytes(Path.Combine(v5HostCodecFixtureDirectory, "new-payload.bin"), v5CodecNewPayload);
WriteV5HostContextFixture(v5TrustedNewPeer, v5OldManifest.SchemaHash,
    Path.Combine(v5HostCodecFixtureDirectory, "old-to-new-context.txt"));
WriteV5HostContextFixture(v5TrustedOldPeer, v5NewManifest.SchemaHash,
    Path.Combine(v5HostCodecFixtureDirectory, "new-to-old-context.txt"));

var (v5NullableRouteSource, _, v5NullableRouteDiagnostics) =
    Generate(BuildV5Source(0, includeAdditions: false).Replace(
        "void Submit(Payload payload)", "void Submit(Payload? payload)", StringComparison.Ordinal),
        "NetLiftV5NullableRoute");
Require(v5NullableRouteSource.Length == 0 &&
        v5NullableRouteDiagnostics.Any(diagnostic =>
            diagnostic.Id == "NLT002" &&
            diagnostic.GetMessage().Contains("exactly one parameter", StringComparison.Ordinal)),
    "A nullable DTO route parameter did not fail with an actionable schema-v5 diagnostic.");

var v5DuplicateFieldSource = BuildV5Source(0, includeAdditions: false).Replace(
    "[property: LiftField(2)] int Quantity",
    "[property: LiftField(1)] int Quantity",
    StringComparison.Ordinal);
var (v5DuplicateFieldGenerated, _, v5DuplicateFieldDiagnostics) =
    Generate(v5DuplicateFieldSource, "NetLiftV5DuplicateField");
Require(v5DuplicateFieldGenerated.Length == 0 &&
        v5DuplicateFieldDiagnostics.Any(diagnostic =>
            diagnostic.Id == "NLT002" &&
            diagnostic.GetMessage().Contains("duplicate field ID", StringComparison.Ordinal)),
    "A duplicate v5 field ID did not report NLT002 and suppress generated source.");

var v5InaccessibleFieldSource = BuildV5Source(0, includeAdditions: false).Replace(
    "int Quantity);",
    "int Quantity)\n        {\n            [LiftField(3)] private string Hidden { get; } = \"hidden\";\n        }",
    StringComparison.Ordinal);
var (v5InaccessibleFieldGenerated, _, v5InaccessibleFieldDiagnostics) =
    Generate(v5InaccessibleFieldSource, "NetLiftV5InaccessibleField");
Require(v5InaccessibleFieldGenerated.Length == 0 &&
        v5InaccessibleFieldDiagnostics.Any(diagnostic =>
            diagnostic.Id == "NLT002" &&
            diagnostic.GetMessage().Contains("must have a public getter", StringComparison.Ordinal)),
    "An inaccessible annotated v5 DTO property did not report NLT002 and suppress generated source.");

using var v5AssemblyStream = new MemoryStream();
var v5Emit = v5NewCompilation.Emit(v5AssemblyStream);
Require(v5Emit.Success,
    "Schema-v5 generated direct dispatch did not compile: " +
    string.Join(Environment.NewLine, v5Emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
v5AssemblyStream.Position = 0;
var v5Assembly = AssemblyLoadContext.Default.LoadFromStream(v5AssemblyStream);
var v5PayloadType = v5Assembly.GetType("Payload", throwOnError: true)!;
var v5Payload = Activator.CreateInstance(v5PayloadType, "SKU-9", 5, "call", null, true)!;
var v5Service = Activator.CreateInstance(v5Assembly.GetType("OrdersService", throwOnError: true)!)!;
v5Assembly.GetType("NetLift.Generated.Dispatch", throwOnError: true)!
    .GetMethod("Dispatch_17_1")!.Invoke(null, [v5Service, v5Payload]);
Require(ReferenceEquals(
        v5Service.GetType().GetProperty("LastSubmitted")!.GetValue(v5Service), v5Payload),
    "Schema-v5 generated dispatch did not directly invoke the managed service.");

var (v5MissingDefaultSource, _, v5MissingDefaultDiagnostics) =
    Generate(BuildV5Source(1, includeAdditions: true).Replace(
        "Optional = true, DefaultValue = \"\"", "Optional = true", StringComparison.Ordinal),
        "NetLiftV5MissingDefault");
Require(v5MissingDefaultSource.Length == 0 &&
        v5MissingDefaultDiagnostics.Any(diagnostic =>
            diagnostic.Id == "NLT002" &&
            diagnostic.GetMessage().Contains("requires an explicit DefaultValue", StringComparison.Ordinal)),
    "An optional field without a declared default did not fail with an actionable diagnostic.");
var (v5UnsupportedTypeSource, _, v5UnsupportedTypeDiagnostics) =
    Generate(BuildV5Source(0, includeAdditions: false).Replace(
        "string Sku", "double Sku", StringComparison.Ordinal),
        "NetLiftV5UnsupportedType");
Require(v5UnsupportedTypeSource.Length == 0 &&
        v5UnsupportedTypeDiagnostics.Any(diagnostic =>
            diagnostic.Id == "NLT002" &&
            diagnostic.GetMessage().Contains("supports only string, int, and bool", StringComparison.Ordinal)),
    "An unsupported schema-v5 field type did not fail with an actionable diagnostic.");
var v5MetadataWithoutOptIn = BuildV5Source(1, includeAdditions: true).Replace(
    "[assembly: LiftContractRevision(1, 1)]", "", StringComparison.Ordinal);
var (v5UnoptedSource, _, v5UnoptedDiagnostics) = Generate(v5MetadataWithoutOptIn, "NetLiftV5Unopted");
Require(v5UnoptedSource.Length == 0 &&
        v5UnoptedDiagnostics.Any(diagnostic =>
            diagnostic.Id == "NLT002" &&
            diagnostic.GetMessage().Contains("requires an assembly-level", StringComparison.Ordinal)),
    "Optional/default metadata without explicit revision opt-in did not fail closed.");
var v5HostModuleSource = BuildV5Source(0, includeAdditions: false).Replace(
    "[assembly: LiftContractRevision(1, 0)]",
    "[assembly: LiftContractRevision(1, 0)]\n[assembly: LiftHostModule]",
    StringComparison.Ordinal);
var (v5HostModuleGenerated, _, v5HostModuleDiagnostics) =
    Generate(v5HostModuleSource, "NetLiftV5HostModule");
Require(v5HostModuleGenerated.Length == 0 &&
        v5HostModuleDiagnostics.Any(diagnostic =>
            diagnostic.Id == "NLT002" &&
            diagnostic.GetMessage().Contains("cannot be combined with [LiftHostModule]", StringComparison.Ordinal)),
    "Unsupported schema-v5/host-module combination did not fail closed.");

var asyncV5OldSource = BuildAsyncV5Source(0, includeAdditions: false);
var asyncV5NewSource = BuildAsyncV5Source(1, includeAdditions: true);
var (asyncV5OldGenerated, asyncV5OldCompilation, asyncV5OldDiagnostics) =
    Generate(asyncV5OldSource, "NetLiftAsyncV5Old");
var (asyncV5NewGenerated, asyncV5NewCompilation, asyncV5NewDiagnostics) =
    Generate(asyncV5NewSource, "NetLiftAsyncV5New");
var (asyncV5OldGeneratedAgain, _, _) = Generate(asyncV5OldSource, "NetLiftAsyncV5Old");
var (asyncV5NewGeneratedAgain, _, _) = Generate(asyncV5NewSource, "NetLiftAsyncV5New");
var asyncV5NewDispatchStart = asyncV5NewGenerated.IndexOf(
    "    public static async global::System.Threading.Tasks.Task<global::SubmitResult > DispatchAsync(",
    StringComparison.Ordinal);
var asyncV5NewProxyFactoryStart = asyncV5NewDispatchStart < 0
    ? -1
    : asyncV5NewGenerated.IndexOf(
        "    public static global::IConfirmation CreateConfirmationProxy()",
        asyncV5NewDispatchStart,
        StringComparison.Ordinal);
var asyncV5NewDispatchSource = asyncV5NewDispatchStart >= 0 && asyncV5NewProxyFactoryStart > asyncV5NewDispatchStart
    ? asyncV5NewGenerated[asyncV5NewDispatchStart..asyncV5NewProxyFactoryStart]
    : "";
Require(asyncV5OldGenerated.Length != 0 && asyncV5NewGenerated.Length != 0 &&
        asyncV5OldGenerated == asyncV5OldGeneratedAgain &&
        asyncV5NewGenerated == asyncV5NewGeneratedAgain &&
        !asyncV5OldDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) &&
        !asyncV5NewDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) &&
        !asyncV5OldCompilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) &&
        !asyncV5NewCompilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) &&
        asyncV5NewGenerated.Contains("Dispatch_17_1", StringComparison.Ordinal) &&
        asyncV5NewGenerated.Contains("service.SubmitAsync(request, cancellationToken)", StringComparison.Ordinal) &&
        asyncV5NewGenerated.Contains("AsyncCallbackV1Host", StringComparison.Ordinal) &&
        asyncV5NewGenerated.Contains("Register<TLease>", StringComparison.Ordinal) &&
        asyncV5NewGenerated.Contains("IAsyncDisposable", StringComparison.Ordinal) &&
        asyncV5NewGenerated.Contains("CreateConfirmationProxy", StringComparison.Ordinal) &&
        asyncV5NewGenerated.Contains("ILiftAsyncCallbackV1NativeServiceInvoker", StringComparison.Ordinal) &&
        asyncV5NewDispatchSource.Contains("Dispatch_17_1(service, request, cancellationToken)", StringComparison.Ordinal) &&
        asyncV5NewDispatchSource.Contains("return result;", StringComparison.Ordinal) &&
        !asyncV5NewDispatchSource.Contains("CreateConfirmationProxy", StringComparison.Ordinal) &&
        !asyncV5NewDispatchSource.Contains("ConfirmAsync", StringComparison.Ordinal) &&
        !asyncV5NewDispatchSource.Contains("Accepted && confirmed", StringComparison.Ordinal) &&
        !asyncV5NewGenerated.Contains("System.Reflection", StringComparison.Ordinal) &&
        !asyncV5NewGenerated.Contains("public static class Codec", StringComparison.Ordinal),
        "AsyncCallbackV1 generated source was not deterministic, valid, or directly dispatchable: " +
        string.Join("; ", asyncV5OldDiagnostics.Concat(asyncV5NewDiagnostics)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => $"{diagnostic.Id}: {diagnostic.GetMessage()}")) +
        " || " + string.Join("; ", asyncV5OldCompilation.GetDiagnostics().Concat(asyncV5NewCompilation.GetDiagnostics())
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.GetMessage())) +
        " || output=" + asyncV5NewGenerated);

var asyncV5OldManifestBytes = Encoding.UTF8.GetBytes(ReadSchema(asyncV5OldGenerated));
var asyncV5NewManifestBytes = Encoding.UTF8.GetBytes(ReadSchema(asyncV5NewGenerated));
var asyncV5OldManifest = NegotiationManifest.LoadEmbedded(asyncV5OldManifestBytes);
var asyncV5NewManifest = NegotiationManifest.LoadEmbedded(asyncV5NewManifestBytes);
Require(asyncV5OldManifest.Profile == NegotiationProfile.AsyncCallbackV1 &&
        asyncV5NewManifest.Profile == NegotiationProfile.AsyncCallbackV1 &&
        asyncV5OldManifest.SchemaHash != asyncV5NewManifest.SchemaHash &&
        asyncV5OldManifest.SchemaHash == "d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b" &&
        asyncV5NewManifest.SchemaHash == "969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4" &&
        asyncV5OldManifest.FeatureBits == 0x36 && asyncV5OldManifest.RequiredFeatureBits == 0x36 &&
        asyncV5NewManifest.FeatureBits == 0x36 && asyncV5NewManifest.RequiredFeatureBits == 0x36 &&
        asyncV5OldManifest.DtoId == 21 && asyncV5OldManifest.ResultDtoId == 22 &&
        asyncV5NewManifest.RequestFields.Count == 3 && asyncV5NewManifest.ResultFields.Count == 3 &&
        asyncV5NewManifest.RequestFields[2].Id == 7 &&
        asyncV5NewManifest.RequestFields[2].Optional &&
        Equals(asyncV5NewManifest.RequestFields[2].DefaultValue, "") &&
        asyncV5NewManifest.ResultFields[2].Id == 9 &&
        asyncV5NewManifest.ResultFields[2].Optional &&
        Equals(asyncV5NewManifest.ResultFields[2].DefaultValue, false),
    "AsyncCallbackV1 did not emit the fixed profile, feature mask, distinct DTO identities, or independent defaults.");

var asyncProjectionRejected = false;
try
{
    _ = ProjectionEmitter.Emit(asyncV5NewGenerated);
}
catch (InvalidDataException exception) when (
    exception.Message.Contains("supported NetLift schema shape", StringComparison.Ordinal))
{
    asyncProjectionRejected = true;
}
Require(asyncProjectionRejected,
    "The default v1-v4 projector unexpectedly accepted the async schema-v5 profile.");

var asyncProjectionWrongProfileRejected = false;
try
{
    _ = ProjectionEmitter.Emit(
        asyncV5NewGenerated.Replace("async-callback-v1", "sync-dto-v1", StringComparison.Ordinal),
        new ProjectionOptions(EmitSchemaV5AsyncHostCodecs: true));
}
catch (InvalidDataException exception) when (
    exception.Message.Contains("profile async-callback-v1", StringComparison.Ordinal))
{
    asyncProjectionWrongProfileRejected = true;
}
Require(asyncProjectionWrongProfileRejected,
    "The async host-codec projector accepted a different profile discriminator.");

var asyncProjectionWrongFeatureRejected = false;
try
{
    var sourceWithWrongFeatures = asyncV5NewGenerated.Replace(
        "featureBits\\\":54", "featureBits\\\":16", StringComparison.Ordinal);
    Require(sourceWithWrongFeatures != asyncV5NewGenerated,
        "The async feature-mask test mutation did not match the canonical manifest.");
    _ = ProjectionEmitter.Emit(
        sourceWithWrongFeatures, new ProjectionOptions(EmitSchemaV5AsyncHostCodecs: true));
}
catch (InvalidDataException exception) when (
    exception.Message.Contains("feature/required-feature masks equal to 0x36", StringComparison.Ordinal))
{
    asyncProjectionWrongFeatureRejected = true;
}
Require(asyncProjectionWrongFeatureRejected,
    "The async host-codec projector accepted a non-0x36 profile feature mask.");

var asyncProjectionUnsupportedWireTypeRejected = false;
try
{
    var sourceWithUnsupportedType = asyncV5NewGenerated.Replace(
        "type\\\":\\\"i32", "type\\\":\\\"f64", StringComparison.Ordinal);
    Require(sourceWithUnsupportedType != asyncV5NewGenerated,
        "The async wire-type test mutation did not match the canonical manifest.");
    _ = ProjectionEmitter.Emit(
        sourceWithUnsupportedType, new ProjectionOptions(EmitSchemaV5AsyncHostCodecs: true));
}
catch (InvalidDataException exception) when (
    exception.Message.Contains("unsupported wire type 'f64'", StringComparison.Ordinal))
{
    asyncProjectionUnsupportedWireTypeRejected = true;
}
Require(asyncProjectionUnsupportedWireTypeRejected,
    "The async host-codec projector accepted an unsupported DTO wire type.");

var asyncProjectionMixedModesRejected = false;
try
{
    _ = ProjectionEmitter.Emit(asyncV5NewGenerated, new ProjectionOptions(
        EmitSchemaV5HostCodecs: true,
        EmitSchemaV5AsyncHostCodecs: true));
}
catch (InvalidDataException exception) when (
    exception.Message.Contains("mutually exclusive", StringComparison.Ordinal))
{
    asyncProjectionMixedModesRejected = true;
}
Require(asyncProjectionMixedModesRejected,
    "The schema-v5 projector accepted mutually exclusive sync and async host-codec modes.");

var (asyncV5Utf8Source, _, asyncV5Utf8Diagnostics) = Generate(
    asyncV5NewSource
        .Replace("[property: LiftField(1)] int Code", "[property: LiftField(1)] string Code", StringComparison.Ordinal)
        .Replace("new SubmitResult(request.Count, true", "new SubmitResult(request.Name, true", StringComparison.Ordinal),
    "NetLiftAsyncV5Utf8");
Require(asyncV5Utf8Source.Length != 0 &&
        !asyncV5Utf8Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
    "The async UTF-8 result codec fixture did not generate cleanly.");

var asyncV5OldHostProjection = ProjectionEmitter.Emit(
    asyncV5OldGenerated, new ProjectionOptions(EmitSchemaV5AsyncHostCodecs: true));
var asyncV5NewHostProjection = ProjectionEmitter.Emit(
    asyncV5NewGenerated, new ProjectionOptions(EmitSchemaV5AsyncHostCodecs: true));
var asyncV5NewHostProjectionAgain = ProjectionEmitter.Emit(
    asyncV5NewGenerated, new ProjectionOptions(EmitSchemaV5AsyncHostCodecs: true));
var asyncV5Utf8HostProjection = ProjectionEmitter.Emit(
    asyncV5Utf8Source, new ProjectionOptions(EmitSchemaV5AsyncHostCodecs: true));
Require(asyncV5OldHostProjection.Keys.OrderBy(key => key, StringComparer.Ordinal)
            .SequenceEqual(new[] { "NetLift.kt", "NetLift.swift" }.OrderBy(key => key, StringComparer.Ordinal)) &&
        asyncV5NewHostProjection.SequenceEqual(asyncV5NewHostProjectionAgain) &&
Fingerprint(asyncV5OldHostProjection["NetLift.swift"]) ==
    "10185ae6c25740e7c05dc702c0a53ad0b828b6cec46289de40f5d05917f5332d" &&
Fingerprint(asyncV5OldHostProjection["NetLift.kt"]) ==
    "962aac5294c94e2e86310ad8a15b588cc0b286de617c8a70c9c7a9c2061eda4f" &&
Fingerprint(asyncV5NewHostProjection["NetLift.swift"]) ==
    "3ee501a4d2908536f1ae2d4e8f2a03013b2333c643ff3a7d9f637366a3efccfe" &&
Fingerprint(asyncV5NewHostProjection["NetLift.kt"]) ==
    "c570316c593e465f996fb48ec4bb987d8eb3780432dacdeda70af81bf2324ff8" &&
Fingerprint(asyncV5Utf8HostProjection["NetLift.swift"]) ==
    "730341762b72ffb6422767726601cbe4f5d328a649fc9829a735f6075257550a" &&
Fingerprint(asyncV5Utf8HostProjection["NetLift.kt"]) ==
    "e181ca2ed751faf393d6e353078635bf43a62d939a36e3b119f0bad6ca360043" &&
        asyncV5OldHostProjection["NetLift.swift"].Contains(
            "decodeResult(", StringComparison.Ordinal) &&
        asyncV5OldHostProjection["NetLift.swift"].Contains(
            "context: ManagedResultSchemaContext", StringComparison.Ordinal) &&
        asyncV5OldHostProjection["NetLift.kt"].Contains(
            "context: ManagedResultSchemaContext", StringComparison.Ordinal) &&
        asyncV5NewHostProjection["NetLift.swift"].Contains(
            "context.requireActive()", StringComparison.Ordinal) &&
        asyncV5NewHostProjection["NetLift.kt"].Contains(
            "context.requireActive()", StringComparison.Ordinal) &&
        asyncV5OldHostProjection["NetLift.swift"].Contains("0x36", StringComparison.Ordinal) &&
        asyncV5OldHostProjection["NetLift.kt"].Contains("0x36L", StringComparison.Ordinal) &&
        !asyncV5OldHostProjection["NetLift.swift"].Contains("testingOnly", StringComparison.Ordinal) &&
        !asyncV5OldHostProjection["NetLift.kt"].Contains("testingOnly", StringComparison.Ordinal) &&
        !asyncV5OldHostProjection["NetLift.swift"].Contains("endpointToken", StringComparison.Ordinal) &&
        !asyncV5OldHostProjection["NetLift.kt"].Contains("endpointToken", StringComparison.Ordinal) &&
        asyncV5Utf8HostProjection["NetLift.swift"].Contains(
            "String(bytes: bytes, encoding: .utf8)", StringComparison.Ordinal) &&
        asyncV5Utf8HostProjection["NetLift.kt"].Contains(
            "CodingErrorAction.REPORT", StringComparison.Ordinal),
    "Async host-bound codecs were nondeterministic or omitted host-bound request/result validation.");

var asyncHostOutput = Path.Combine(AppContext.BaseDirectory, "projection-schema-v5-async-host-codecs");
var asyncHostFixtureDirectory = Path.Combine(asyncHostOutput, "fixtures");
Directory.CreateDirectory(asyncHostFixtureDirectory);
File.WriteAllText(Path.Combine(asyncHostFixtureDirectory, "old-contract.g.cs"), asyncV5OldGenerated);
File.WriteAllText(Path.Combine(asyncHostFixtureDirectory, "new-contract.g.cs"), asyncV5NewGenerated);
foreach (var (projectionName, projection) in new[]
         {
             ("old", asyncV5OldHostProjection),
             ("new", asyncV5NewHostProjection),
             ("utf8", asyncV5Utf8HostProjection)
         })
{
    var projectionDirectory = Path.Combine(asyncHostOutput, projectionName);
    Directory.CreateDirectory(projectionDirectory);
    File.WriteAllText(Path.Combine(projectionDirectory, "NetLift.swift"), projection["NetLift.swift"]);
    File.WriteAllText(Path.Combine(projectionDirectory, "NetLift.kt"), projection["NetLift.kt"]);
}

var asyncV5Utf8Manifest = NegotiationManifest.LoadEmbedded(
    Encoding.UTF8.GetBytes(ReadSchema(asyncV5Utf8Source)));
WriteAsyncV5PeerContextFixture(asyncV5OldManifest, asyncV5NewManifest.SchemaHash,
    Path.Combine(asyncHostFixtureDirectory, "old-request-context.txt"));
WriteAsyncV5PeerContextFixture(asyncV5NewManifest, asyncV5OldManifest.SchemaHash,
    Path.Combine(asyncHostFixtureDirectory, "new-request-context.txt"));
WriteAsyncV5ResultContextFixture(asyncV5OldManifest, asyncV5OldManifest.SchemaHash,
    Path.Combine(asyncHostFixtureDirectory, "old-result-context.txt"));
WriteAsyncV5ResultContextFixture(asyncV5NewManifest, asyncV5OldManifest.SchemaHash,
    Path.Combine(asyncHostFixtureDirectory, "old-codec-new-managed-result-context.txt"));
WriteAsyncV5ResultContextFixture(asyncV5NewManifest, asyncV5NewManifest.SchemaHash,
    Path.Combine(asyncHostFixtureDirectory, "new-result-context.txt"));
WriteAsyncV5ResultContextFixture(asyncV5OldManifest, asyncV5NewManifest.SchemaHash,
    Path.Combine(asyncHostFixtureDirectory, "new-codec-old-managed-result-context.txt"));
WriteAsyncV5PeerContextFixture(asyncV5Utf8Manifest, asyncV5Utf8Manifest.SchemaHash,
    Path.Combine(asyncHostFixtureDirectory, "utf8-request-context.txt"));
WriteAsyncV5ResultContextFixture(asyncV5Utf8Manifest, asyncV5Utf8Manifest.SchemaHash,
    Path.Combine(asyncHostFixtureDirectory, "utf8-result-context.txt"));

File.WriteAllBytes(Path.Combine(asyncHostFixtureDirectory, "old-request.bin"),
    BuildNlf1Payload(
        (1, 1, Encoding.UTF8.GetBytes("SKU-OLD")),
        (2, 2, Int32LittleEndian(19))));
File.WriteAllBytes(Path.Combine(asyncHostFixtureDirectory, "new-request.bin"),
    BuildNlf1Payload(
        (1, 1, Encoding.UTF8.GetBytes("SKU-NEW")),
        (2, 2, Int32LittleEndian(42)),
        (7, 1, Encoding.UTF8.GetBytes("note"))));
File.WriteAllBytes(Path.Combine(asyncHostFixtureDirectory, "new-request-default.bin"),
    BuildNlf1Payload(
        (1, 1, Encoding.UTF8.GetBytes("SKU-DEFAULT")),
        (2, 2, Int32LittleEndian(1)),
        (7, 1, [])));
File.WriteAllBytes(Path.Combine(asyncHostFixtureDirectory, "old-result.bin"),
    BuildNlf1Payload((1, 2, Int32LittleEndian(201)), (2, 3, [1])));
File.WriteAllBytes(Path.Combine(asyncHostFixtureDirectory, "new-result.bin"),
    BuildNlf1Payload((1, 2, Int32LittleEndian(202)), (2, 3, [1]), (9, 3, [1])));
File.WriteAllBytes(Path.Combine(asyncHostFixtureDirectory, "utf8-result.bin"),
    BuildNlf1Payload(
        (1, 1, Encoding.UTF8.GetBytes("utf8-ok")),
        (2, 3, [1]),
        (9, 3, [0])));
File.WriteAllBytes(Path.Combine(asyncHostFixtureDirectory, "utf8-invalid-result.bin"),
    BuildNlf1Payload((1, 1, [0xff]), (2, 3, [1])));

var asyncExactHashStart = new CompatibleStartReference(asyncV5OldManifestBytes, true, 1, 0).Start(
    asyncV5OldManifestBytes,
    CompatibleStartPeerHelloV1.Encode(asyncV5OldManifest),
    new PeerManifestTrust(asyncV5OldManifest.SchemaHash, 1, 0),
    peerCompatibleStartOptIn: false);
Require(asyncExactHashStart.Succeeded &&
        asyncExactHashStart.Contract!.Mode == NegotiationMode.Strict,
    "Exact-hash AsyncCallbackV1 start incorrectly required additive-start opt-in.");

var asyncV5OldEndpoint = new CompatibleStartReference(asyncV5OldManifestBytes, true, 1, 0);
var asyncV5NewEndpoint = new CompatibleStartReference(asyncV5NewManifestBytes, true, 1, 0);
var asyncV5OldStart = asyncV5OldEndpoint.Start(
    asyncV5NewManifestBytes,
    CompatibleStartPeerHelloV1.Encode(asyncV5NewManifest),
    new PeerManifestTrust(asyncV5NewManifest.SchemaHash, 1, 0),
    peerCompatibleStartOptIn: true);
var asyncV5NewStart = asyncV5NewEndpoint.Start(
    asyncV5OldManifestBytes,
    CompatibleStartPeerHelloV1.Encode(asyncV5OldManifest),
    new PeerManifestTrust(asyncV5OldManifest.SchemaHash, 1, 0),
    peerCompatibleStartOptIn: true);
Require(asyncV5OldStart.Succeeded && asyncV5NewStart.Succeeded &&
        asyncV5OldStart.Contract!.Mode == NegotiationMode.AdditiveOptionalFields &&
        asyncV5NewStart.Contract!.Mode == NegotiationMode.AdditiveOptionalFields,
    "Trusted bilateral AsyncCallbackV1 negotiation failed in an old/new direction.");

var asyncNewRequestPayload = asyncV5NewStart.Contract!.EncodeRequestDto(
    new Dictionary<ushort, object?> { [1] = "new-call", [2] = 7, [7] = "new-only" });
var asyncOldRequestDecoded = asyncV5OldStart.Contract!.DecodeRequestDto(asyncNewRequestPayload);
Require(asyncOldRequestDecoded.Count == 2 &&
        Equals(asyncOldRequestDecoded[1], "new-call") && Equals(asyncOldRequestDecoded[2], 7),
    "Old request decoder did not skip precisely the newer optional request addition.");

var asyncOldRequestPayload = asyncV5OldStart.Contract.EncodeRequestDto(
    new Dictionary<ushort, object?> { [1] = "old-call", [2] = 8 });
var asyncNewRequestDecoded = asyncV5NewStart.Contract.DecodeRequestDto(asyncOldRequestPayload);
Require(asyncNewRequestDecoded.Count == 3 &&
        Equals(asyncNewRequestDecoded[1], "old-call") && Equals(asyncNewRequestDecoded[2], 8) &&
        Equals(asyncNewRequestDecoded[7], ""),
    "New request decoder did not materialize its local optional default.");

var asyncOldResultPayload = asyncV5OldStart.Contract.EncodeResultDto(
    new Dictionary<ushort, object?> { [1] = 41, [2] = true });
var asyncNewResultDecoded = asyncV5NewStart.Contract.DecodeResultDto(asyncOldResultPayload);
Require(asyncNewResultDecoded.Count == 3 &&
        Equals(asyncNewResultDecoded[1], 41) && Equals(asyncNewResultDecoded[2], true) &&
        Equals(asyncNewResultDecoded[9], false),
    "New result decoder did not materialize its local optional result default.");

var asyncNewResultPayload = asyncV5NewStart.Contract.EncodeResultDto(
    new Dictionary<ushort, object?> { [1] = 42, [2] = true, [9] = true });
var asyncOldResultDecoded = asyncV5OldStart.Contract.DecodeResultDto(asyncNewResultPayload);
Require(asyncOldResultDecoded.Count == 2 &&
        Equals(asyncOldResultDecoded[1], 42) && Equals(asyncOldResultDecoded[2], true),
    "Old result decoder did not independently skip the newer optional result addition.");

using var asyncV5AssemblyStream = new MemoryStream();
var asyncV5Emit = asyncV5OldCompilation.Emit(asyncV5AssemblyStream);
Require(asyncV5Emit.Success,
    "AsyncCallbackV1 generated direct dispatcher did not compile: " +
    string.Join(Environment.NewLine,
        asyncV5Emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
asyncV5AssemblyStream.Position = 0;
var asyncV5Assembly = AssemblyLoadContext.Default.LoadFromStream(asyncV5AssemblyStream);
var asyncRequestType = asyncV5Assembly.GetType("SubmitInput", throwOnError: true)!;
var asyncRequest = Activator.CreateInstance(asyncRequestType,
    asyncOldRequestDecoded[1], asyncOldRequestDecoded[2])!;
var asyncService = Activator.CreateInstance(
    asyncV5Assembly.GetType("OrdersService", throwOnError: true)!)!;
using var asyncCancellation = new CancellationTokenSource();
var directNativeInvoker = new AsyncCallbackV1TestNativeInvoker();
var directHostDescriptor = AsyncCallbackV1HostTestSupport.CreateDescriptor(
    asyncV5OldManifest, asyncV5NewManifest, endpointToken: 101);
var directInvocationContext = new LiftInvocationContext(
    asyncCancellation.Token, 7000, directNativeInvoker)
{
    EndpointToken = directHostDescriptor.EndpointToken,
    RegistrationToken = 8000,
    RegistrationGeneration = 2,
    OperationToken = 9000,
    PeerRequestSchema = directHostDescriptor.PeerRequestSchema,
    ManagedResultSchema = directHostDescriptor.ManagedResultSchema
};
var asyncDispatchTask = (Task)asyncV5Assembly.GetType(
        "NetLift.Generated.AsyncCallbackV1Host", throwOnError: true)!
    .GetMethod("DispatchAsync")!
    .Invoke(null, [asyncRequest, asyncService, directInvocationContext, asyncCancellation.Token])!;
asyncDispatchTask.GetAwaiter().GetResult();
var asyncResult = asyncDispatchTask.GetType().GetProperty("Result")!.GetValue(asyncDispatchTask)!;
Require(Equals(asyncService.GetType().GetProperty("LastRequest")!.GetValue(asyncService), asyncRequest) &&
        Equals(asyncService.GetType().GetProperty("LastCancellationToken")!.GetValue(asyncService),
            asyncCancellation.Token) &&
        Equals(asyncResult.GetType().GetProperty("Code")!.GetValue(asyncResult), 7) &&
        Equals(asyncResult.GetType().GetProperty("Accepted")!.GetValue(asyncResult), true) &&
        directNativeInvoker.CallCount == 1 &&
        directNativeInvoker.LastPrompt == "new-call",
    "Generated AsyncCallbackV1 dispatch did not scope the context for service-owned callback dispatch: " +
    $"request={asyncService.GetType().GetProperty("LastRequest")!.GetValue(asyncService) == asyncRequest}, " +
    $"token={asyncService.GetType().GetProperty("LastCancellationToken")!.GetValue(asyncService)}, " +
    $"code={asyncResult.GetType().GetProperty("Code")!.GetValue(asyncResult)}, " +
    $"accepted={asyncResult.GetType().GetProperty("Accepted")!.GetValue(asyncResult)}, " +
    $"callbackCount={directNativeInvoker.CallCount}, prompt={directNativeInvoker.LastPrompt}");

using var asyncV5NewAssemblyStream = new MemoryStream();
var asyncV5NewEmit = asyncV5NewCompilation.Emit(asyncV5NewAssemblyStream);
Require(asyncV5NewEmit.Success,
    "AsyncCallbackV1 new generated host registration did not compile: " +
    string.Join(Environment.NewLine,
        asyncV5NewEmit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
asyncV5NewAssemblyStream.Position = 0;
var asyncV5NewAssembly = AssemblyLoadContext.Default.LoadFromStream(asyncV5NewAssemblyStream);

var asyncHostType = asyncV5Assembly.GetType("NetLift.Generated.AsyncCallbackV1Host", throwOnError: true)!;
var asyncNewHostType = asyncV5NewAssembly.GetType("NetLift.Generated.AsyncCallbackV1Host", throwOnError: true)!;
var asyncHostInstanceRegister = GetInstanceRegisterMethod(asyncHostType);
var asyncNewHostInstanceRegister = GetInstanceRegisterMethod(asyncNewHostType);
var oldHostDescriptor = AsyncCallbackV1HostTestSupport.CreateDescriptor(
    asyncV5OldManifest, asyncV5NewManifest, endpointToken: 101);
var oldHostRegistry = new AsyncCallbackV1TestRegistry(oldHostDescriptor);
var oldHostService = Activator.CreateInstance(
    asyncV5Assembly.GetType("OrdersService", throwOnError: true)!)!;
var oldRegistration = (IDisposable)asyncHostInstanceRegister.Invoke(
    null, [oldHostRegistry, oldHostService])!;
Require(oldHostRegistry.RegistrationCount == 1 &&
        oldHostRegistry.RegisteredServiceId == 17 &&
        oldHostRegistry.RegisteredMethodId == 1 &&
        oldHostRegistry.Handler is not null &&
        oldHostService.GetType().GetProperty("LastRequest")!.GetValue(oldHostService) is null,
    "Generated Register did not install exactly the declared async managed route.");

var oldNativeInvoker = new AsyncCallbackV1TestNativeInvoker
{
    DeferredReply = new TaskCompletionSource<LiftNativeReply>(
        TaskCreationOptions.RunContinuationsAsynchronously)
};
using var oldOperationCancellation = new CancellationTokenSource();
var oldInvocationContext = new LiftInvocationContext(
    oldOperationCancellation.Token, 7001, oldNativeInvoker)
{
    EndpointToken = oldHostDescriptor.EndpointToken,
    RegistrationToken = 8001,
    RegistrationGeneration = 3,
    OperationToken = 9001,
    PeerRequestSchema = oldHostDescriptor.PeerRequestSchema,
    ManagedResultSchema = oldHostDescriptor.ManagedResultSchema
};
var asyncDecodedOldRequest = asyncHostType.GetMethod("DecodeRequest")!.Invoke(
    null, [(ReadOnlyMemory<byte>)asyncNewRequestPayload, oldInvocationContext])!;
Require((string)asyncDecodedOldRequest.GetType().GetProperty("Name")!.GetValue(asyncDecodedOldRequest)! == "new-call" &&
        (int)asyncDecodedOldRequest.GetType().GetProperty("Count")!.GetValue(asyncDecodedOldRequest)! == 7,
    "Generated old request decoder did not consume canonical NLF1 under the trusted NLPS peer descriptor.");

var generatedBoundaryRequestPayload = asyncV5NewStart.Contract.EncodeRequestDto(
    new Dictionary<ushort, object?> { [1] = new string('x', 4066), [2] = 1 });
Require(generatedBoundaryRequestPayload.Length == LiftAsyncCallbackV1Wire.MaximumPayloadBytes,
    "The generated route boundary fixture did not equal the exact AsyncCallbackV1 payload cap.");
var generatedBoundaryRequest = asyncHostType.GetMethod("DecodeRequest")!.Invoke(
    null, [(ReadOnlyMemory<byte>)generatedBoundaryRequestPayload, oldInvocationContext])!;
Require(((string)generatedBoundaryRequest.GetType().GetProperty("Name")!.GetValue(generatedBoundaryRequest)!).Length == 4066,
    "Generated NLF1 decoding rejected the exact 4096-byte request boundary.");
var oversizedGeneratedRequestPayload = BuildNlf1Payload(
    (1, 1, Encoding.UTF8.GetBytes(new string('x', 4067))),
    (2, 2, Int32LittleEndian(1)));
var oversizedGeneratedRequestRejected = false;
try
{
    _ = asyncHostType.GetMethod("DecodeRequest")!.Invoke(
        null, [(ReadOnlyMemory<byte>)oversizedGeneratedRequestPayload, oldInvocationContext]);
}
catch (TargetInvocationException exception) when (
    exception.InnerException is LiftAsyncCallbackV1ContractException)
{
    oversizedGeneratedRequestRejected = true;
}
Require(oversizedGeneratedRequestPayload.Length == 4097 && oversizedGeneratedRequestRejected,
    "Generated NLF1 decoding did not reject a request over the exact 4096-byte cap.");

var oldRouteTask = oldHostRegistry.InvokeAsync(
    asyncNewRequestPayload, oldInvocationContext, oldOperationCancellation.Token).AsTask();
await Task.Yield();
Require(!oldRouteTask.IsCompleted && oldNativeInvoker.CallCount == 1 &&
        oldNativeInvoker.LastServiceId == 30 && oldNativeInvoker.LastMethodId == 1 &&
        oldNativeInvoker.LastPrompt == "new-call" &&
        oldNativeInvoker.LastInvocationContext.RegistrationToken == 8001 &&
        oldNativeInvoker.LastInvocationContext.RegistrationGeneration == 3 &&
        oldNativeInvoker.LastInvocationContext.OperationToken == 9001 &&
        oldNativeInvoker.LastInvocationContext.CorrelationToken == 7001,
    "Generated route did not await the typed callback while preserving V2 invocation identity.");
oldNativeInvoker.DeferredReply.SetResult(new LiftNativeReply(
    LiftNativeReplyKind.Result, new byte[] { 1 }, ReadOnlyMemory<byte>.Empty));
var oldEncodedResult = await oldRouteTask;
var decodedOldResultOnNewPeer = asyncV5NewStart.Contract!.DecodeResultDto(oldEncodedResult.Span);
var expectedOldEncodedResult = asyncV5OldStart.Contract!.EncodeResultDto(
    new Dictionary<ushort, object?> { [1] = 7, [2] = true });
Require(oldEncodedResult.Span.SequenceEqual(expectedOldEncodedResult) &&
        Equals(decodedOldResultOnNewPeer[1], 7) &&
        Equals(decodedOldResultOnNewPeer[2], true) &&
        Equals(decodedOldResultOnNewPeer[9], false),
    "Generated old route did not emit canonical NLF1 bytes under live NLMR or preserve new-peer defaults.");

oldNativeInvoker.DeferredReply = null;
oldNativeInvoker.NextReply = new LiftNativeReply(
    LiftNativeReplyKind.Result, new byte[] { 0 }, ReadOnlyMemory<byte>.Empty);
using var oldDeclineCancellation = new CancellationTokenSource();
var oldDeclineContext = oldInvocationContext with
{
    CancellationToken = oldDeclineCancellation.Token,
    CorrelationToken = 7002,
    OperationToken = 9002
};
var oldDeclineResult = await oldHostRegistry.InvokeAsync(
    asyncOldRequestPayload, oldDeclineContext, oldDeclineCancellation.Token);
var decodedDeclineOnNewPeer = asyncV5NewStart.Contract.DecodeResultDto(oldDeclineResult.Span);
Require(Equals(decodedDeclineOnNewPeer[2], false),
    "Generated typed confirmation decline did not produce Accepted=false.");

var wrongEndpointContext = oldDeclineContext with { EndpointToken = 999 };
var wrongEndpointRejected = false;
try
{
    _ = await oldHostRegistry.InvokeAsync(
        asyncOldRequestPayload, wrongEndpointContext, oldDeclineCancellation.Token);
}
catch (LiftAsyncCallbackV1ContractException)
{
    wrongEndpointRejected = true;
}
Require(wrongEndpointRejected,
    "Generated registration accepted a route invocation from another endpoint.");

var malformedRoutePayloadRejected = false;
try
{
    _ = await oldHostRegistry.InvokeAsync(
        new byte[12], oldDeclineContext, oldDeclineCancellation.Token);
}
catch (LiftAsyncCallbackV1ContractException)
{
    malformedRoutePayloadRejected = true;
}
Require(malformedRoutePayloadRejected && oldNativeInvoker.CallCount == 2,
    "Generated route did not reject malformed NLF1 before invoking the typed native callback.");

var oldServiceLastRequest = oldHostService.GetType().GetProperty("LastRequest")!.GetValue(oldHostService);
var invalidUtf8RoutePayload = (byte[])asyncNewRequestPayload.Clone();
invalidUtf8RoutePayload[19] = 0xff;
var invalidUtf8RouteRejected = false;
try
{
    _ = await oldHostRegistry.InvokeAsync(
        invalidUtf8RoutePayload, oldDeclineContext, oldDeclineCancellation.Token);
}
catch (LiftAsyncCallbackV1ContractException)
{
    invalidUtf8RouteRejected = true;
}
Require(invalidUtf8RouteRejected && oldNativeInvoker.CallCount == 2 &&
        ReferenceEquals(
            oldServiceLastRequest,
            oldHostService.GetType().GetProperty("LastRequest")!.GetValue(oldHostService)),
    "Generated request decoder accepted malformed UTF-8 or dispatched it before validation.");

oldNativeInvoker.DeferredReply = new TaskCompletionSource<LiftNativeReply>(
    TaskCreationOptions.RunContinuationsAsynchronously);
using var cancelledCallbackSource = new CancellationTokenSource();
var cancelledCallbackContext = oldDeclineContext with
{
    CancellationToken = cancelledCallbackSource.Token,
    CorrelationToken = 7004,
    OperationToken = 9004
};
var cancelledCallbackTask = oldHostRegistry.InvokeAsync(
    asyncOldRequestPayload, cancelledCallbackContext, cancelledCallbackSource.Token).AsTask();
await Task.Yield();
Require(!cancelledCallbackTask.IsCompleted &&
        oldNativeInvoker.LastCancellationToken == cancelledCallbackSource.Token,
    "Generated native callback invocation did not remain asynchronous or forward cancellation.");
cancelledCallbackSource.Cancel();
var operationCancellationObserved = false;
try
{
    _ = await cancelledCallbackTask;
}
catch (OperationCanceledException)
{
    operationCancellationObserved = true;
}
Require(operationCancellationObserved,
    "Generated async route did not propagate cancellation while awaiting the native callback.");
oldNativeInvoker.DeferredReply = null;

oldNativeInvoker.NextReply = new LiftNativeReply(
    LiftNativeReplyKind.Result, new byte[] { 2 }, ReadOnlyMemory<byte>.Empty);
var malformedReplyRejected = false;
try
{
    _ = await oldHostRegistry.InvokeAsync(
        asyncOldRequestPayload, oldDeclineContext, oldDeclineCancellation.Token);
}
catch (LiftNativeServiceException exception) when (exception.StatusCode == 7)
{
    malformedReplyRejected = true;
}
Require(malformedReplyRejected,
    "Generated typed callback adapter accepted a malformed bool reply.");

oldNativeInvoker.NextReply = new LiftNativeReply(
    LiftNativeReplyKind.HandlerFailure,
    ReadOnlyMemory<byte>.Empty,
    Encoding.UTF8.GetBytes("confirmation failed"));
var callbackErrorMapped = false;
try
{
    _ = await oldHostRegistry.InvokeAsync(
        asyncOldRequestPayload, oldDeclineContext, oldDeclineCancellation.Token);
}
catch (LiftNativeServiceException exception) when (
    exception.StatusCode == 13 && exception.DiagnosticUtf8.Span.SequenceEqual(
        Encoding.UTF8.GetBytes("confirmation failed")))
{
    callbackErrorMapped = true;
}
Require(callbackErrorMapped,
    "Generated typed callback adapter lost the native handler failure status or diagnostic.");

oldNativeInvoker.NextReply = new LiftNativeReply(
    LiftNativeReplyKind.Cancelled, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
var callbackCancellationMapped = false;
try
{
    _ = await oldHostRegistry.InvokeAsync(
        asyncOldRequestPayload, oldDeclineContext, oldDeclineCancellation.Token);
}
catch (OperationCanceledException)
{
    callbackCancellationMapped = true;
}
Require(callbackCancellationMapped,
    "Generated typed callback adapter did not map native cancellation to OperationCanceledException.");

var legacyNativeInvoker = new AsyncCallbackV1LegacyTestInvoker();
var legacyInvokerContext = oldDeclineContext with { NativeServices = legacyNativeInvoker };
var legacyInvokerRejected = false;
try
{
    _ = await oldHostRegistry.InvokeAsync(
        asyncOldRequestPayload, legacyInvokerContext, oldDeclineCancellation.Token);
}
catch (LiftAsyncCallbackV1ContractException)
{
    legacyInvokerRejected = true;
}
Require(legacyInvokerRejected,
    "Generated callback proxy silently used a legacy invoker that cannot preserve V2 operation identity.");

var callbacksBeforeServiceDecline = oldNativeInvoker.CallCount;
oldNativeInvoker.NextReply = new LiftNativeReply(
    LiftNativeReplyKind.Result, new byte[] { 1 }, ReadOnlyMemory<byte>.Empty);
var serviceDeclinePayload = asyncV5NewStart.Contract.EncodeRequestDto(
    new Dictionary<ushort, object?> { [1] = "decline", [2] = 9, [7] = "service-declined" });
var serviceDeclineResult = await oldHostRegistry.InvokeAsync(
    serviceDeclinePayload, oldDeclineContext, oldDeclineCancellation.Token);
var decodedServiceDecline = asyncV5NewStart.Contract.DecodeResultDto(serviceDeclineResult.Span);
Require(oldNativeInvoker.CallCount == callbacksBeforeServiceDecline + 1 &&
        oldNativeInvoker.LastPrompt == "decline" &&
        Equals(decodedServiceDecline[2], false),
    "The managed service did not own its confirmation call and business decline decision.");

oldRegistration.Dispose();
Require(oldHostRegistry.Handler is null,
    "Disposing the generated registration did not withdraw the managed route.");

var nullInstanceRegistry = new AsyncCallbackV1TestRegistry(oldHostDescriptor);
var nullInstanceRejected = false;
try
{
    _ = asyncHostInstanceRegister.Invoke(null, [nullInstanceRegistry, null]);
}
catch (TargetInvocationException exception) when (
    exception.InnerException is ArgumentNullException argumentException &&
    argumentException.ParamName == "service")
{
    nullInstanceRejected = true;
}
Require(nullInstanceRejected && nullInstanceRegistry.RegistrationCount == 0,
    "Adding the generic Register overload changed null-instance overload behavior.");

var factoryRegisterMethod = asyncHostType.GetMethods()
    .Single(method => method.Name == "Register" && method.IsGenericMethodDefinition)
    .MakeGenericMethod(typeof(AsyncCallbackV1TestServiceLease));
var nullFactoryRegistry = new AsyncCallbackV1TestRegistry(oldHostDescriptor);
var nullFactoryRejected = false;
try
{
    _ = factoryRegisterMethod.Invoke(null, [nullFactoryRegistry, null]);
}
catch (TargetInvocationException exception) when (
    exception.InnerException is ArgumentNullException argumentException &&
    argumentException.ParamName == "serviceFactory")
{
    nullFactoryRejected = true;
}
Require(nullFactoryRejected && nullFactoryRegistry.RegistrationCount == 0,
    "Generated Register accepted a null per-operation service factory.");

var dynamicOrdersInterface = asyncV5Assembly.GetType("IOrders", throwOnError: true)!;
var dynamicOrdersService = asyncV5Assembly.GetType("OrdersService", throwOnError: true)!;
var factoryDescriptor = AsyncCallbackV1HostTestSupport.CreateDescriptor(
    asyncV5OldManifest, asyncV5NewManifest, endpointToken: 111);
var factoryRegistry = new AsyncCallbackV1TestRegistry(factoryDescriptor);
var factoryServices = new ConcurrentDictionary<ulong, (object Service, AsyncCallbackV1TestServiceLease Lease)>();
var factoryContexts = new ConcurrentDictionary<ulong, LiftInvocationContext>();
var factoryCancellationTokens = new ConcurrentDictionary<ulong, CancellationToken>();
var factoryCallCount = 0;
var firstLeaseDisposeEntered = new TaskCompletionSource<bool>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var firstLeaseDisposeGate = new TaskCompletionSource<bool>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var perOperationFactory = AsyncCallbackV1HostTestSupport.CreateServiceFactoryDelegate(
    dynamicOrdersInterface,
    (context, cancellationToken) =>
    {
        Interlocked.Increment(ref factoryCallCount);
        var service = Activator.CreateInstance(dynamicOrdersService)!;
        var lease = new AsyncCallbackV1TestServiceLease();
        if (context.OperationToken == 9101)
        {
            lease.DisposeAction = () =>
            {
                firstLeaseDisposeEntered.TrySetResult(true);
                return new ValueTask(firstLeaseDisposeGate.Task);
            };
        }
        factoryServices[context.OperationToken] = (service, lease);
        factoryContexts[context.OperationToken] = context;
        factoryCancellationTokens[context.OperationToken] = cancellationToken;
        return (service, lease);
    });
var factoryRegistration = (IDisposable)factoryRegisterMethod.Invoke(
    null, [factoryRegistry, perOperationFactory])!;
Require(factoryCallCount == 0 && factoryRegistry.RegistrationCount == 1,
    "Generated per-operation Register instantiated a service while registering the route.");

var malformedFactoryContext = CreateAsyncCallbackV1ContextForTest(
    factoryDescriptor, new AsyncCallbackV1TestNativeInvoker(), CancellationToken.None,
    correlationToken: 7100, registrationToken: 8100, generation: 5, operationToken: 9100);
var malformedFactoryRequestRejected = false;
try
{
    _ = await factoryRegistry.InvokeAsync(
        new byte[12], malformedFactoryContext, CancellationToken.None);
}
catch (LiftAsyncCallbackV1ContractException)
{
    malformedFactoryRequestRejected = true;
}
Require(malformedFactoryRequestRejected && factoryCallCount == 0,
    "Generated per-operation Register acquired a service before request validation.");

var firstFactoryInvoker = new AsyncCallbackV1TestNativeInvoker
{
    DeferredReply = new TaskCompletionSource<LiftNativeReply>(
        TaskCreationOptions.RunContinuationsAsynchronously)
};
var secondFactoryInvoker = new AsyncCallbackV1TestNativeInvoker
{
    DeferredReply = new TaskCompletionSource<LiftNativeReply>(
        TaskCreationOptions.RunContinuationsAsynchronously)
};
using var firstFactoryCancellation = new CancellationTokenSource();
using var secondFactoryCancellation = new CancellationTokenSource();
var firstFactoryContext = CreateAsyncCallbackV1ContextForTest(
    factoryDescriptor, firstFactoryInvoker, firstFactoryCancellation.Token,
    correlationToken: 7101, registrationToken: 8101, generation: 5, operationToken: 9101);
var secondFactoryContext = CreateAsyncCallbackV1ContextForTest(
    factoryDescriptor, secondFactoryInvoker, secondFactoryCancellation.Token,
    correlationToken: 7102, registrationToken: 8102, generation: 6, operationToken: 9102);
var firstFactoryTask = factoryRegistry.InvokeAsync(
    asyncNewRequestPayload, firstFactoryContext, firstFactoryCancellation.Token).AsTask();
var secondFactoryTask = factoryRegistry.InvokeAsync(
    asyncOldRequestPayload, secondFactoryContext, secondFactoryCancellation.Token).AsTask();
await Task.Yield();
var firstFactoryValue = factoryServices[9101];
var secondFactoryValue = factoryServices[9102];
Require(!firstFactoryTask.IsCompleted && !secondFactoryTask.IsCompleted &&
        factoryCallCount == 2 &&
        firstFactoryInvoker.CallCount == 1 && firstFactoryInvoker.LastPrompt == "new-call" &&
        secondFactoryInvoker.CallCount == 1 && secondFactoryInvoker.LastPrompt == "old-call" &&
        firstFactoryInvoker.LastInvocationContext.OperationToken == 9101 &&
        secondFactoryInvoker.LastInvocationContext.OperationToken == 9102 &&
        !ReferenceEquals(firstFactoryValue.Service, secondFactoryValue.Service) &&
        firstFactoryValue.Lease.DisposeCount == 0 && secondFactoryValue.Lease.DisposeCount == 0 &&
        factoryContexts[9101].CorrelationToken == 7101 &&
        factoryContexts[9102].CorrelationToken == 7102 &&
        factoryCancellationTokens[9101] == firstFactoryCancellation.Token &&
        factoryCancellationTokens[9102] == secondFactoryCancellation.Token,
    $"Concurrent factory route checks failed: factoryCalls={factoryCallCount}, " +
    $"pending={(!firstFactoryTask.IsCompleted)}/{(!secondFactoryTask.IsCompleted)}, " +
    $"callbacks={firstFactoryInvoker.CallCount}/{secondFactoryInvoker.CallCount}, " +
    $"prompts={firstFactoryInvoker.LastPrompt}/{secondFactoryInvoker.LastPrompt}, " +
    $"operations={firstFactoryInvoker.LastInvocationContext.OperationToken}/{secondFactoryInvoker.LastInvocationContext.OperationToken}, " +
    $"distinctServices={!ReferenceEquals(firstFactoryValue.Service, secondFactoryValue.Service)}, " +
    $"leases={firstFactoryValue.Lease.DisposeCount}/{secondFactoryValue.Lease.DisposeCount}, " +
    $"correlations={factoryContexts[9101].CorrelationToken}/{factoryContexts[9102].CorrelationToken}, " +
    $"cancellationTokens={factoryCancellationTokens[9101] == firstFactoryCancellation.Token}/" +
    $"{factoryCancellationTokens[9102] == secondFactoryCancellation.Token}.");
firstFactoryInvoker.DeferredReply.SetResult(
    new LiftNativeReply(LiftNativeReplyKind.Result, new byte[] { 1 }, ReadOnlyMemory<byte>.Empty));
secondFactoryInvoker.DeferredReply.SetResult(
    new LiftNativeReply(LiftNativeReplyKind.Result, new byte[] { 0 }, ReadOnlyMemory<byte>.Empty));
await firstLeaseDisposeEntered.Task;
var secondFactoryResult = await secondFactoryTask;
Require(!firstFactoryTask.IsCompleted &&
        firstFactoryValue.Lease.DisposeCount == 1 &&
        secondFactoryValue.Lease.DisposeCount == 1,
    "A factory route completed before async lease disposal or disposed a lease more than once.");
firstLeaseDisposeGate.SetResult(true);
var firstFactoryResult = await firstFactoryTask;
var decodedFirstFactoryResult = asyncV5NewStart.Contract.DecodeResultDto(firstFactoryResult.Span);
var decodedSecondFactoryResult = asyncV5NewStart.Contract.DecodeResultDto(secondFactoryResult.Span);
Require(Equals(decodedFirstFactoryResult[1], 7) &&
        Equals(decodedFirstFactoryResult[2], true) &&
        Equals(decodedSecondFactoryResult[1], 8) &&
        Equals(decodedSecondFactoryResult[2], false) &&
        firstFactoryValue.Lease.DisposeCount == 1 &&
        secondFactoryValue.Lease.DisposeCount == 1,
    "Factory route output or per-operation lease disposal did not match its service and callback.");
var invocationContextRestored = false;
try
{
    _ = LiftInvocationContextAccessor.Current;
}
catch (InvalidOperationException exception) when (
    exception.Message == "No NetLift invocation context is active.")
{
    invocationContextRestored = true;
}
Require(invocationContextRestored,
    "Concurrent factory route completion leaked an invocation context into its caller.");
factoryRegistration.Dispose();

var factoryFailureRegistry = new AsyncCallbackV1TestRegistry(
    AsyncCallbackV1HostTestSupport.CreateDescriptor(
        asyncV5OldManifest, asyncV5NewManifest, endpointToken: 112));
var throwingFactory = AsyncCallbackV1HostTestSupport.CreateServiceFactoryDelegate(
    dynamicOrdersInterface,
    (_, _) => throw new InvalidOperationException("factory acquisition failed"));
var factoryFailureRegistration = (IDisposable)factoryRegisterMethod.Invoke(
    null, [factoryFailureRegistry, throwingFactory])!;
var factoryFailureContext = CreateAsyncCallbackV1ContextForTest(
    factoryFailureRegistry.Descriptor, new AsyncCallbackV1TestNativeInvoker(),
    CancellationToken.None, 7201, 8201, 7, 9201);
var factoryFailureDistinct = false;
try
{
    _ = await factoryFailureRegistry.InvokeAsync(
        asyncNewRequestPayload, factoryFailureContext, CancellationToken.None);
}
catch (LiftAsyncCallbackV1ServiceFactoryException exception) when (
    exception.InnerException is InvalidOperationException inner &&
    inner.Message == "factory acquisition failed")
{
    factoryFailureDistinct = true;
}
Require(factoryFailureDistinct,
    "A thrown service factory exception was not preserved as a distinct acquisition failure.");
factoryFailureRegistration.Dispose();

var invalidFactoryRegistry = new AsyncCallbackV1TestRegistry(
    AsyncCallbackV1HostTestSupport.CreateDescriptor(
        asyncV5OldManifest, asyncV5NewManifest, endpointToken: 113));
var invalidFactoryLease = new AsyncCallbackV1TestServiceLease();
var nullServiceFactory = AsyncCallbackV1HostTestSupport.CreateServiceFactoryDelegate(
    dynamicOrdersInterface,
    (_, _) => (null, invalidFactoryLease));
var invalidFactoryRegistration = (IDisposable)factoryRegisterMethod.Invoke(
    null, [invalidFactoryRegistry, nullServiceFactory])!;
var invalidFactoryContext = CreateAsyncCallbackV1ContextForTest(
    invalidFactoryRegistry.Descriptor, new AsyncCallbackV1TestNativeInvoker(),
    CancellationToken.None, 7202, 8202, 7, 9202);
var invalidNullServiceDistinct = false;
try
{
    _ = await invalidFactoryRegistry.InvokeAsync(
        asyncNewRequestPayload, invalidFactoryContext, CancellationToken.None);
}
catch (LiftAsyncCallbackV1InvalidServiceLeaseException exception) when (
    exception.Message.Contains("null service", StringComparison.Ordinal))
{
    invalidNullServiceDistinct = true;
}
Require(invalidNullServiceDistinct && invalidFactoryLease.DisposeCount == 1,
    "A null factory service was not reported distinctly or its returned lease was not disposed.");
invalidFactoryRegistration.Dispose();

var nullLeaseFactoryRegistry = new AsyncCallbackV1TestRegistry(
    AsyncCallbackV1HostTestSupport.CreateDescriptor(
        asyncV5OldManifest, asyncV5NewManifest, endpointToken: 114));
var nullLeaseFactory = AsyncCallbackV1HostTestSupport.CreateServiceFactoryDelegate(
    dynamicOrdersInterface,
    (_, _) => (Activator.CreateInstance(dynamicOrdersService), null));
var nullLeaseFactoryRegistration = (IDisposable)factoryRegisterMethod.Invoke(
    null, [nullLeaseFactoryRegistry, nullLeaseFactory])!;
var nullLeaseContext = CreateAsyncCallbackV1ContextForTest(
    nullLeaseFactoryRegistry.Descriptor, new AsyncCallbackV1TestNativeInvoker(),
    CancellationToken.None, 7203, 8203, 7, 9203);
var invalidNullLeaseDistinct = false;
try
{
    _ = await nullLeaseFactoryRegistry.InvokeAsync(
        asyncNewRequestPayload, nullLeaseContext, CancellationToken.None);
}
catch (LiftAsyncCallbackV1InvalidServiceLeaseException exception) when (
    exception.Message.Contains("null lease", StringComparison.Ordinal))
{
    invalidNullLeaseDistinct = true;
}
Require(invalidNullLeaseDistinct,
    "A factory result without a disposable lease was not reported distinctly.");
nullLeaseFactoryRegistration.Dispose();

var disposeFailureRegistry = new AsyncCallbackV1TestRegistry(
    AsyncCallbackV1HostTestSupport.CreateDescriptor(
        asyncV5OldManifest, asyncV5NewManifest, endpointToken: 115));
var disposeFailureLease = new AsyncCallbackV1TestServiceLease
{
    DisposeAction = () => ValueTask.FromException(new InvalidOperationException("dispose after success failed"))
};
var disposeFailureFactory = AsyncCallbackV1HostTestSupport.CreateServiceFactoryDelegate(
    dynamicOrdersInterface,
    (_, _) => (Activator.CreateInstance(dynamicOrdersService), disposeFailureLease));
var disposeFailureRegistration = (IDisposable)factoryRegisterMethod.Invoke(
    null, [disposeFailureRegistry, disposeFailureFactory])!;
var disposeFailureInvoker = new AsyncCallbackV1TestNativeInvoker();
var disposeFailureContext = CreateAsyncCallbackV1ContextForTest(
    disposeFailureRegistry.Descriptor, disposeFailureInvoker,
    CancellationToken.None, 7204, 8204, 7, 9204);
var successfulDispatchDisposalFailureObserved = false;
try
{
    _ = await disposeFailureRegistry.InvokeAsync(
        asyncNewRequestPayload, disposeFailureContext, CancellationToken.None);
}
catch (LiftAsyncCallbackV1ServiceLeaseDisposalException exception) when (
    exception.InnerException is InvalidOperationException inner &&
    inner.Message == "dispose after success failed")
{
    successfulDispatchDisposalFailureObserved = true;
}
Require(successfulDispatchDisposalFailureObserved &&
        disposeFailureInvoker.CallCount == 1 &&
        disposeFailureLease.DisposeCount == 1,
    "A lease disposal failure after success returned an encoded response or was hidden.");
disposeFailureRegistration.Dispose();

var cancelWithDisposeRegistry = new AsyncCallbackV1TestRegistry(
    AsyncCallbackV1HostTestSupport.CreateDescriptor(
        asyncV5OldManifest, asyncV5NewManifest, endpointToken: 116));
var cancelWithDisposeLease = new AsyncCallbackV1TestServiceLease();
var cancelWithDisposeFactory = AsyncCallbackV1HostTestSupport.CreateServiceFactoryDelegate(
    dynamicOrdersInterface,
    (_, _) => (Activator.CreateInstance(dynamicOrdersService), cancelWithDisposeLease));
var cancelWithDisposeRegistration = (IDisposable)factoryRegisterMethod.Invoke(
    null, [cancelWithDisposeRegistry, cancelWithDisposeFactory])!;
var cancelWithDisposeInvoker = new AsyncCallbackV1TestNativeInvoker
{
    DeferredReply = new TaskCompletionSource<LiftNativeReply>(
        TaskCreationOptions.RunContinuationsAsynchronously)
};
using var cancelWithDisposeSource = new CancellationTokenSource();
var cancelWithDisposeContext = CreateAsyncCallbackV1ContextForTest(
    cancelWithDisposeRegistry.Descriptor, cancelWithDisposeInvoker,
    cancelWithDisposeSource.Token, 7205, 8205, 7, 9205);
var cancelWithDisposeRuntime = new AsyncCallbackV1TestRuntime();
var cancelWithDisposeTask = cancelWithDisposeRuntime.InvokeAsync(
    cancelWithDisposeRegistry, asyncNewRequestPayload,
    cancelWithDisposeContext, cancelWithDisposeSource.Token).AsTask();
await Task.Yield();
Require(!cancelWithDisposeTask.IsCompleted && cancelWithDisposeInvoker.CallCount == 1,
    "Factory route did not await the native callback before cancellation.");
cancelWithDisposeSource.Cancel();
var cancelWithDisposeResult = await cancelWithDisposeTask;
Require(cancelWithDisposeResult.Status == LiftDispatchStatus.Cancelled &&
        cancelWithDisposeRuntime.LoggedFailures.Count == 0 &&
        cancelWithDisposeLease.DisposeCount == 1,
    "Factory route did not dispose its lease before preserving successful cancellation.");
cancelWithDisposeRegistration.Dispose();

var cancellationDisposalRegistry = new AsyncCallbackV1TestRegistry(
    AsyncCallbackV1HostTestSupport.CreateDescriptor(
        asyncV5OldManifest, asyncV5NewManifest, endpointToken: 117));
var cancellationDisposalLease = new AsyncCallbackV1TestServiceLease
{
    DisposeAction = () => ValueTask.FromException(new InvalidOperationException("dispose during cancellation failed"))
};
var cancellationDisposalFactory = AsyncCallbackV1HostTestSupport.CreateServiceFactoryDelegate(
    dynamicOrdersInterface,
    (_, _) => (Activator.CreateInstance(dynamicOrdersService), cancellationDisposalLease));
var cancellationDisposalRegistration = (IDisposable)factoryRegisterMethod.Invoke(
    null, [cancellationDisposalRegistry, cancellationDisposalFactory])!;
var cancellationDisposalInvoker = new AsyncCallbackV1TestNativeInvoker
{
    DeferredReply = new TaskCompletionSource<LiftNativeReply>(
        TaskCreationOptions.RunContinuationsAsynchronously)
};
using var cancellationDisposalSource = new CancellationTokenSource();
var cancellationDisposalContext = CreateAsyncCallbackV1ContextForTest(
    cancellationDisposalRegistry.Descriptor, cancellationDisposalInvoker,
    cancellationDisposalSource.Token, 7206, 8206, 8, 9206);
var testRuntime = new AsyncCallbackV1TestRuntime();
var cancellationDisposalTask = testRuntime.InvokeAsync(
    cancellationDisposalRegistry, asyncNewRequestPayload,
    cancellationDisposalContext, cancellationDisposalSource.Token).AsTask();
await Task.Yield();
Require(!cancellationDisposalTask.IsCompleted && cancellationDisposalInvoker.CallCount == 1,
    "Factory route did not remain pending before cancellation.");
cancellationDisposalSource.Cancel();
var cancellationDisposalResult = await cancellationDisposalTask;
var cancellationDisposalAggregate = testRuntime.LoggedFailures.SingleOrDefault() as AggregateException;
Require(cancellationDisposalResult.Status == LiftDispatchStatus.ManagedFailure &&
        cancellationDisposalAggregate is not null &&
        cancellationDisposalAggregate.InnerExceptions[0] is OperationCanceledException originalCancellation &&
        originalCancellation.CancellationToken == cancellationDisposalSource.Token &&
        cancellationDisposalAggregate.InnerExceptions[1] is LiftAsyncCallbackV1ServiceLeaseDisposalException
        {
            InnerException: InvalidOperationException { Message: "dispose during cancellation failed" }
        } &&
        cancellationDisposalLease.DisposeCount == 1,
    "The test runtime hid a disposal failure or misreported it as cancellation.");
cancellationDisposalRegistration.Dispose();

var newHostDescriptor = AsyncCallbackV1HostTestSupport.CreateDescriptor(
    asyncV5NewManifest, asyncV5OldManifest, endpointToken: 102);
var newHostRegistry = new AsyncCallbackV1TestRegistry(newHostDescriptor);
var newHostService = Activator.CreateInstance(
    asyncV5NewAssembly.GetType("OrdersService", throwOnError: true)!)!;
var newRegistration = (IDisposable)asyncNewHostInstanceRegister.Invoke(
    null, [newHostRegistry, newHostService])!;
var newNativeInvoker = new AsyncCallbackV1TestNativeInvoker
{
    NextReply = new LiftNativeReply(
        LiftNativeReplyKind.Result, new byte[] { 0 }, ReadOnlyMemory<byte>.Empty)
};
using var newOperationCancellation = new CancellationTokenSource();
var newInvocationContext = new LiftInvocationContext(
    newOperationCancellation.Token, 7003, newNativeInvoker)
{
    EndpointToken = newHostDescriptor.EndpointToken,
    RegistrationToken = 8002,
    RegistrationGeneration = 4,
    OperationToken = 9003,
    PeerRequestSchema = newHostDescriptor.PeerRequestSchema,
    ManagedResultSchema = newHostDescriptor.ManagedResultSchema
};
var asyncDecodedNewRequest = asyncNewHostType.GetMethod("DecodeRequest")!.Invoke(
    null, [(ReadOnlyMemory<byte>)asyncOldRequestPayload, newInvocationContext])!;
Require((string)asyncDecodedNewRequest.GetType().GetProperty("Name")!.GetValue(asyncDecodedNewRequest)! == "old-call" &&
        (int)asyncDecodedNewRequest.GetType().GetProperty("Count")!.GetValue(asyncDecodedNewRequest)! == 8 &&
        (string)asyncDecodedNewRequest.GetType().GetProperty("Note")!.GetValue(asyncDecodedNewRequest)! == "",
    "Generated new request decoder did not apply the optional default under the trusted old NLPS descriptor.");
var newEncodedResult = await newHostRegistry.InvokeAsync(
    asyncOldRequestPayload, newInvocationContext, newOperationCancellation.Token);
var decodedNewResultOnOldPeer = asyncV5OldStart.Contract.DecodeResultDto(newEncodedResult.Span);
var expectedNewEncodedResult = asyncV5NewStart.Contract.EncodeResultDto(
    new Dictionary<ushort, object?> { [1] = 8, [2] = false, [9] = false });
Require(newEncodedResult.Span.SequenceEqual(expectedNewEncodedResult) &&
        Equals(decodedNewResultOnOldPeer[1], 8) &&
        Equals(decodedNewResultOnOldPeer[2], false),
    "Generated new route did not emit canonical NLF1 bytes under local NLMR or allow old-peer optional-field skipping.");
newRegistration.Dispose();

var wrongProfileRegistry = new AsyncCallbackV1TestRegistry(
    oldHostDescriptor with { ProfileId = 2 });
var wrongProfileRejected = false;
try
{
    _ = asyncHostInstanceRegister.Invoke(
        null, [wrongProfileRegistry, oldHostService]);
}
catch (TargetInvocationException exception) when (
    exception.InnerException is LiftAsyncCallbackV1ContractException)
{
    wrongProfileRejected = true;
}
Require(wrongProfileRejected && wrongProfileRegistry.RegistrationCount == 0,
    "Generated Register installed a route for a non-AsyncCallbackV1 host profile.");
var badResultDescriptor = oldHostDescriptor.ManagedResultSchema.ToArray();
badResultDescriptor[16] ^= 1;
var invalidHostDescriptors = new[]
{
    oldHostDescriptor with { ManagedMethodId = 2 },
    oldHostDescriptor with { RequiredFeatureBits = 0x16 },
    oldHostDescriptor with { LocalSchemaHash = new byte[32] },
    oldHostDescriptor with { ManagedResultSchema = badResultDescriptor }
};
foreach (var invalidDescriptor in invalidHostDescriptors)
{
    var invalidRegistry = new AsyncCallbackV1TestRegistry(invalidDescriptor);
    var invalidBindingRejected = false;
    try
    {
        _ = asyncHostInstanceRegister.Invoke(
            null, [invalidRegistry, oldHostService]);
    }
    catch (TargetInvocationException exception) when (
        exception.InnerException is LiftAsyncCallbackV1ContractException)
    {
        invalidBindingRejected = true;
    }
    Require(invalidBindingRejected && invalidRegistry.RegistrationCount == 0,
        "Generated Register installed a route for a wrong route, feature mask, hash, or NLMR descriptor.");
}

ExpectNegotiationException(
    () => asyncV5OldStart.Contract.EncodeDto(new Dictionary<ushort, object?>()),
    NegotiationErrorCode.WireInvalid,
    "ambiguous sync DTO encoding on AsyncCallbackV1");
ExpectNegotiationException(
    () => asyncV5OldStart.Contract.DecodeDto(asyncOldRequestPayload),
    NegotiationErrorCode.WireInvalid,
    "ambiguous sync DTO decoding on AsyncCallbackV1");

var asyncInvalidUtf8Payload = asyncV5NewStart.Contract.EncodeRequestDto(
    new Dictionary<ushort, object?> { [1] = "abc", [2] = 1 });
asyncInvalidUtf8Payload[19] = 0xff;
ExpectNegotiationException(
    () => asyncV5OldStart.Contract.DecodeRequestDto(asyncInvalidUtf8Payload),
    NegotiationErrorCode.WireInvalid,
    "malformed AsyncCallbackV1 UTF-8");
ExpectNegotiationException(
    () => asyncV5OldStart.Contract.DecodeRequestDto(asyncNewRequestPayload[..^1]),
    NegotiationErrorCode.WireInvalid,
    "truncated AsyncCallbackV1 DTO");
var asyncUnknownFieldPayload = (byte[])asyncNewRequestPayload.Clone();
BinaryPrimitives.WriteUInt16LittleEndian(asyncUnknownFieldPayload.AsSpan(12), 99);
ExpectNegotiationException(
    () => asyncV5OldStart.Contract.DecodeRequestDto(asyncUnknownFieldPayload),
    NegotiationErrorCode.WireInvalid,
    "AsyncCallbackV1 field absent from the trusted peer descriptor");
var asyncBoundaryPayload = asyncV5OldStart.Contract.EncodeRequestDto(
    new Dictionary<ushort, object?> { [1] = new string('x', 4066), [2] = 1 });
Require(asyncBoundaryPayload.Length == 4096,
    "AsyncCallbackV1 did not accept the exact 4096-byte DTO payload bound.");
ExpectNegotiationException(
    () => asyncV5OldStart.Contract.EncodeRequestDto(
        new Dictionary<ushort, object?> { [1] = new string('x', 4067), [2] = 1 }),
    NegotiationErrorCode.WireInvalid,
    "AsyncCallbackV1 DTO exceeding the 4096-byte payload bound");
var v5LargeSyncPayload = oldCompatibleStart.Contract!.EncodeDto(
    new Dictionary<ushort, object?> { [1] = new string('s', 5000), [2] = 1 });
Require(v5LargeSyncPayload.Length > 4096 &&
        Equals(oldCompatibleStart.Contract.DecodeDto(v5LargeSyncPayload)[1], new string('s', 5000)),
    "The AsyncCallbackV1 payload cap unexpectedly changed the existing sync-v5 payload bound.");

var asyncOneSidedOptOut = new CompatibleStartReference(asyncV5OldManifestBytes, true, 1, 0)
    .Start(asyncV5NewManifestBytes, CompatibleStartPeerHelloV1.Encode(asyncV5NewManifest),
        new PeerManifestTrust(asyncV5NewManifest.SchemaHash, 1, 0),
        peerCompatibleStartOptIn: false);
Require(!asyncOneSidedOptOut.Succeeded && asyncOneSidedOptOut.ErrorCode == NegotiationErrorCode.OptedOut,
    "AsyncCallbackV1 additive negotiation accepted unilateral opt-in.");
var asyncRevisionFloor = new CompatibleStartReference(asyncV5OldManifestBytes, true, 1, 2)
    .Start(asyncV5NewManifestBytes, CompatibleStartPeerHelloV1.Encode(asyncV5NewManifest),
        new PeerManifestTrust(asyncV5NewManifest.SchemaHash, 1, 0),
        peerCompatibleStartOptIn: true);
Require(!asyncRevisionFloor.Succeeded &&
        asyncRevisionFloor.ErrorCode == NegotiationErrorCode.UntrustedPeerManifest,
    "AsyncCallbackV1 accepted a peer below the configured revision floor.");
var syncAsyncMismatch = NegotiationPreflight.Start(
    v5OldManifest,
    NegotiationManifest.LoadTrustedPeer(asyncV5OldManifestBytes,
        new PeerManifestTrust(asyncV5OldManifest.SchemaHash, 1, 0)),
    asyncV5OldManifest.SchemaHash, asyncV5OldManifest.FeatureBits,
    localCompatibleStartOptIn: true, peerCompatibleStartOptIn: true,
    minimumPeerContractMajor: 1, minimumPeerContractMinor: 0);
Require(!syncAsyncMismatch.Succeeded &&
        syncAsyncMismatch.ErrorCode == NegotiationErrorCode.IncompatibleContract,
    "Sync-v5 and AsyncCallbackV1 profiles unexpectedly negotiated.");

var syncWithLifecycleV2Bit = MutateManifest(v5NewManifestBytes, schema =>
{
    schema["featureBits"] = 0x30;
    schema["requiredFeatureBits"] = 0;
});
ExpectManifestFailure(() => NegotiationManifest.LoadEmbedded(syncWithLifecycleV2Bit),
    NegotiationErrorCode.FeatureMismatch, "sync-v5 recognizing the profile-gated lifecycle-v2 bit");
var asyncWrongMaskBytes = MutateManifest(asyncV5NewManifestBytes, schema =>
{
    schema["featureBits"] = 0x16;
    schema["requiredFeatureBits"] = 0x16;
});
ExpectManifestFailure(() => NegotiationManifest.LoadEmbedded(asyncWrongMaskBytes),
    NegotiationErrorCode.FeatureMismatch, "AsyncCallbackV1 missing lifecycle-v2 feature bit");
var asyncSameDtoIds = MutateManifest(asyncV5NewManifestBytes, schema =>
    schema["method"]!["resultDtoId"] = 21);
ExpectManifestFailure(() => NegotiationManifest.LoadEmbedded(asyncSameDtoIds),
    NegotiationErrorCode.InvalidManifest, "AsyncCallbackV1 repeated request/result DTO identity");
var asyncWrongCallbackName = MutateManifest(asyncV5NewManifestBytes, schema =>
    schema["nativeServices"]![0]!["methods"]![0]!["name"] = "OtherAsync");
ExpectManifestFailure(() => NegotiationManifest.LoadEmbedded(asyncWrongCallbackName),
    NegotiationErrorCode.InvalidManifest, "AsyncCallbackV1 callback shape");
var asyncNullableInt = MutateManifest(asyncV5NewManifestBytes, schema =>
    schema["dtos"]![0]!["fields"]![1]!["nullable"] = true);
ExpectManifestFailure(() => NegotiationManifest.LoadEmbedded(asyncNullableInt),
    NegotiationErrorCode.InvalidManifest, "AsyncCallbackV1 nullable non-string field");
var asyncWrongCallbackInput = MutateManifest(asyncV5NewManifestBytes, schema =>
    schema["nativeServices"]![0]!["methods"]![0]!["inputType"] = "i32");
ExpectManifestFailure(() => NegotiationManifest.LoadEmbedded(asyncWrongCallbackInput),
    NegotiationErrorCode.InvalidManifest, "AsyncCallbackV1 callback input type");
var asyncChangedExistingResultField = MutateManifest(asyncV5NewManifestBytes, schema =>
    schema["dtos"]![1]!["fields"]![1]!["name"] = "Approved");
var asyncChangedExistingResultManifest = NegotiationManifest.LoadTrustedPeer(
    asyncChangedExistingResultField,
    new PeerManifestTrust(ReadManifestHash(asyncChangedExistingResultField), 1, 0));
var asyncChangedExistingResultPair = NegotiationPreflight.Start(
    asyncV5OldManifest,
    asyncChangedExistingResultManifest,
    asyncChangedExistingResultManifest.SchemaHash, asyncChangedExistingResultManifest.FeatureBits,
    localCompatibleStartOptIn: true, peerCompatibleStartOptIn: true,
    minimumPeerContractMajor: 1, minimumPeerContractMinor: 0);
Require(!asyncChangedExistingResultPair.Succeeded &&
        asyncChangedExistingResultPair.ErrorCode == NegotiationErrorCode.IncompatibleContract,
    "AsyncCallbackV1 accepted a changed existing result field.");
var asyncRequiredRequestAddition = MutateManifest(asyncV5NewManifestBytes, schema =>
{
    schema["dtos"]![0]!["fields"]![2]!["optional"] = false;
    schema["dtos"]![0]!["fields"]![2]!["default"] = null;
});
var asyncRequiredRequestManifest = NegotiationManifest.LoadTrustedPeer(
    asyncRequiredRequestAddition,
    new PeerManifestTrust(ReadManifestHash(asyncRequiredRequestAddition), 1, 0));
var asyncRequiredRequestPair = NegotiationPreflight.Start(
    asyncV5OldManifest,
    asyncRequiredRequestManifest,
    asyncRequiredRequestManifest.SchemaHash, asyncRequiredRequestManifest.FeatureBits,
    localCompatibleStartOptIn: true, peerCompatibleStartOptIn: true,
    minimumPeerContractMajor: 1, minimumPeerContractMinor: 0);
Require(!asyncRequiredRequestPair.Succeeded &&
        asyncRequiredRequestPair.ErrorCode == NegotiationErrorCode.IncompatibleContract,
    "AsyncCallbackV1 accepted a required request-field addition.");
var asyncRequiredResultAddition = MutateManifest(asyncV5NewManifestBytes, schema =>
{
    schema["dtos"]![1]!["fields"]![2]!["optional"] = false;
    schema["dtos"]![1]!["fields"]![2]!["default"] = null;
});
var asyncRequiredResultManifest = NegotiationManifest.LoadTrustedPeer(
    asyncRequiredResultAddition,
    new PeerManifestTrust(ReadManifestHash(asyncRequiredResultAddition), 1, 0));
var asyncRequiredResultPair = NegotiationPreflight.Start(
    asyncV5OldManifest,
    asyncRequiredResultManifest,
    asyncRequiredResultManifest.SchemaHash, asyncRequiredResultManifest.FeatureBits,
    localCompatibleStartOptIn: true, peerCompatibleStartOptIn: true,
    minimumPeerContractMajor: 1, minimumPeerContractMinor: 0);
Require(!asyncRequiredResultPair.Succeeded &&
        asyncRequiredResultPair.ErrorCode == NegotiationErrorCode.IncompatibleContract,
    "AsyncCallbackV1 accepted a required result-field addition.");

var asyncMissingRevisionSource = asyncV5OldSource.Replace(
    "[assembly: LiftContractRevision(1, 0)]", "", StringComparison.Ordinal);
var (asyncMissingRevisionGenerated, _, asyncMissingRevisionDiagnostics) =
    Generate(asyncMissingRevisionSource, "NetLiftAsyncV5MissingRevision");
Require(asyncMissingRevisionGenerated.Length == 0 &&
        asyncMissingRevisionDiagnostics.Any(diagnostic =>
            diagnostic.Id == "NLT002" &&
            diagnostic.GetMessage().Contains("requires an assembly-level", StringComparison.Ordinal)),
    "AsyncCallbackV1 profile without contract revision did not fail closed.");
var asyncUnsupportedFieldSource = asyncV5OldSource.Replace(
    "string Name", "double Name", StringComparison.Ordinal);
var (asyncUnsupportedFieldGenerated, _, asyncUnsupportedFieldDiagnostics) =
    Generate(asyncUnsupportedFieldSource, "NetLiftAsyncV5UnsupportedField");
Require(asyncUnsupportedFieldGenerated.Length == 0 &&
        asyncUnsupportedFieldDiagnostics.Any(diagnostic => diagnostic.Id == "NLT002"),
    "AsyncCallbackV1 accepted an unsupported DTO field type.");
var asyncWrongCallbackNameSource = asyncV5OldSource.Replace(
    "ConfirmAsync(string prompt", "OtherAsync(string prompt", StringComparison.Ordinal);
var (asyncWrongCallbackNameGenerated, _, asyncWrongCallbackNameDiagnostics) =
    Generate(asyncWrongCallbackNameSource, "NetLiftAsyncV5WrongCallbackName");
Require(asyncWrongCallbackNameGenerated.Length == 0 &&
        asyncWrongCallbackNameDiagnostics.Any(diagnostic =>
            diagnostic.Id == "NLT002" &&
            diagnostic.GetMessage().Contains("must be named ConfirmAsync", StringComparison.Ordinal)),
    "AsyncCallbackV1 accepted a native callback method with the wrong name.");
var asyncMissingPromptFieldSource = asyncV5OldSource.Replace(
    "string Name", "string OrderCode", StringComparison.Ordinal);
var (asyncMissingPromptFieldGenerated, _, asyncMissingPromptFieldDiagnostics) =
    Generate(asyncMissingPromptFieldSource, "NetLiftAsyncV5MissingPromptField");
Require(asyncMissingPromptFieldGenerated.Length == 0 &&
        asyncMissingPromptFieldDiagnostics.Any(diagnostic =>
            diagnostic.Id == "NLT002" &&
            diagnostic.GetMessage().Contains("field named Name", StringComparison.Ordinal)),
    "AsyncCallbackV1 accepted a request DTO without a typed confirmation prompt field.");
var asyncMissingAcceptedFieldSource = asyncV5OldSource.Replace(
    "bool Accepted", "bool Approved", StringComparison.Ordinal);
var (asyncMissingAcceptedFieldGenerated, _, asyncMissingAcceptedFieldDiagnostics) =
    Generate(asyncMissingAcceptedFieldSource, "NetLiftAsyncV5MissingAcceptedField");
Require(asyncMissingAcceptedFieldGenerated.Length == 0 &&
        asyncMissingAcceptedFieldDiagnostics.Any(diagnostic =>
            diagnostic.Id == "NLT002" &&
            diagnostic.GetMessage().Contains("field named Accepted", StringComparison.Ordinal)),
    "AsyncCallbackV1 accepted a result DTO without a typed confirmation outcome field.");

var asyncProfileDirectory = Path.Combine(AppContext.BaseDirectory, "async-callback-v1");
Directory.CreateDirectory(asyncProfileDirectory);
File.WriteAllText(Path.Combine(asyncProfileDirectory, "old-contract.g.cs"), asyncV5OldGenerated);
File.WriteAllText(Path.Combine(asyncProfileDirectory, "new-contract.g.cs"), asyncV5NewGenerated);
File.WriteAllBytes(Path.Combine(asyncProfileDirectory, "old-manifest.json"), asyncV5OldManifestBytes);
File.WriteAllBytes(Path.Combine(asyncProfileDirectory, "new-manifest.json"), asyncV5NewManifestBytes);
Console.WriteLine($"AsyncCallbackV1 schema-v5 hashes: old={asyncV5OldManifest.SchemaHash} new={asyncV5NewManifest.SchemaHash}");

HostModuleTests.Run();
Console.WriteLine("PASS: deterministic sync/async/native schema/IDs/hash, generator-owned sync/async schema-v5 manifests and directional additive wire proof, generated DTO codecs and native adapter route/error checks, AsyncCallbackV1 host registration/NLF1 dispatch/typed confirmation, unsupported-type diagnostics, direct dispatch, native projections, reserved/colliding native names, opt-in generated host registration, Kotlin/Swift flat DTO codecs, and the opt-in Kotlin service facade.");

static (string GeneratedSource, Compilation OutputCompilation, ImmutableArray<Diagnostic> Diagnostics) Generate(
    string source,
    string assemblyName = "NetLiftFixture")
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
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    GeneratorDriver driver = CSharpGeneratorDriver.Create(
        [new LiftContractGenerator().AsSourceGenerator()],
        parseOptions: new CSharpParseOptions(LanguageVersion.Preview));
    driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
    var generatedSources = driver.GetRunResult().Results.Single().GeneratedSources
        .Where(generated => generated.HintName == "LiftContract.g.cs").ToArray();
    var generatedSource = generatedSources.Length == 0
        ? ""
        : generatedSources[0].SourceText.ToString();
    return (generatedSource, output, diagnostics);
}

static string BuildV5Source(int minor, bool includeAdditions)
{
    var additions = includeAdditions
        ? """
        ,
            [property: LiftField(7, Optional = true, DefaultValue = "")] string Note,
            [property: LiftField(8, Optional = true, DefaultValue = null)] string? NullableNote,
            [property: LiftField(9, Optional = true, DefaultValue = false)] bool GiftWrap
        """
        : "";
    return $$"""
        #nullable enable
        using NetLift;
        [assembly: LiftContractRevision(1, {{minor}})]

        [LiftDto("Payload", Id = 21, Version = 1)]
        public sealed record Payload(
            [property: LiftField(1)] string Sku,
            [property: LiftField(2)] int Quantity{{additions}});

        [LiftService("Orders", Id = 17, Version = 1)]
        public interface IOrders
        {
            [LiftMethod(1)]
            void Submit(Payload payload);
        }

        public sealed class OrdersService : IOrders
        {
            public Payload? LastSubmitted { get; private set; }
            public void Submit(Payload payload) => LastSubmitted = payload;
        }
        """;
}

static string BuildAsyncV5Source(int minor, bool includeAdditions)
{
    var requestAddition = includeAdditions
        ? ",\n            [property: LiftField(7, Optional = true, DefaultValue = \"\")] string Note"
        : "";
    var resultAddition = includeAdditions
        ? ",\n            [property: LiftField(9, Optional = true, DefaultValue = false)] bool Audited"
        : "";
    var resultConstruction = includeAdditions ? ", false" : "";
    return $$"""
        #nullable enable
        using NetLift;
        using System.Threading;
        using System.Threading.Tasks;
        [assembly: LiftContractRevision(1, {{minor}})]
        [assembly: LiftContractProfile(LiftContractProfile.AsyncCallbackV1)]

        [LiftDto("SubmitInput", Id = 21, Version = 1)]
        public sealed record SubmitInput(
            [property: LiftField(1)] string Name,
            [property: LiftField(2)] int Count{{requestAddition}});

        [LiftDto("SubmitResult", Id = 22, Version = 1)]
        public sealed record SubmitResult(
            [property: LiftField(1)] int Code,
            [property: LiftField(2)] bool Accepted{{resultAddition}});

        [LiftService("Orders", Id = 17, Version = 1)]
        public interface IOrders
        {
            [LiftMethod(1)]
            Task<SubmitResult> SubmitAsync(SubmitInput request, CancellationToken cancellationToken);
        }

        [LiftNativeService("Confirmation", Id = 30, Version = 1,
            ReplyProtocol = LiftNativeReplyProtocol.TypedV1)]
        public interface IConfirmation
        {
            [LiftMethod(1)]
            Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken);
        }

        public sealed class OrdersService : IOrders
        {
            public SubmitInput? LastRequest { get; private set; }
            public CancellationToken LastCancellationToken { get; private set; }
            public async Task<SubmitResult> SubmitAsync(SubmitInput request, CancellationToken cancellationToken)
            {
                LastRequest = request;
                LastCancellationToken = cancellationToken;
                var confirmed = await global::NetLift.Generated.AsyncCallbackV1Host.CreateConfirmationProxy()
                    .ConfirmAsync(request.Name, cancellationToken).ConfigureAwait(false);
                return new SubmitResult(request.Count, request.Name != "decline" && confirmed{{resultConstruction}});
            }
        }

        public static class AsyncCallbackV1OverloadCompatibility
        {
            public static global::System.IDisposable RegisterNull(
                global::NetLift.ILiftAsyncCallbackV1HostRegistry registry) =>
                global::NetLift.Generated.AsyncCallbackV1Host.Register(registry, null!);
        }
        """;
}

static void WriteV5PeerSchemaFixture(NegotiationManifest manifest, string path)
{
    var builder = new StringBuilder()
        .Append("schemaHash=").AppendLine(manifest.SchemaHash);
    foreach (var field in manifest.Fields)
    {
        builder.Append("field=")
            .Append(field.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|')
            .Append(field.Type).Append('|')
            .Append(field.Nullable ? '1' : '0').Append('|')
            .Append(field.Optional ? '1' : '0').AppendLine();
    }
    File.WriteAllText(path, builder.ToString());
}

static void WriteV5HostContextFixture(
    NegotiationManifest peer,
    string localSchemaHash,
    string path)
{
    var builder = new StringBuilder()
        .Append("localSchemaHash=").AppendLine(localSchemaHash)
        .Append("schemaHash=").AppendLine(peer.SchemaHash)
        .Append("abiMajor=").AppendLine(peer.AbiMajor.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Append("schemaVersion=").AppendLine(peer.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Append("contractMajor=").AppendLine(peer.ContractMajor.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Append("contractMinor=").AppendLine(peer.ContractMinor.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Append("featureBits=").AppendLine(peer.FeatureBits.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Append("requiredFeatureBits=").AppendLine(peer.RequiredFeatureBits.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Append("serviceID=").AppendLine(peer.ServiceId.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Append("methodID=").AppendLine(peer.MethodId.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Append("dtoID=").AppendLine(peer.DtoId.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Append("dtoVersion=").AppendLine(peer.DtoVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    foreach (var field in peer.Fields)
    {
        builder.Append("field=")
            .Append(field.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|')
            .Append(field.Type).Append('|')
            .Append(field.Nullable ? '1' : '0').Append('|')
            .Append(field.Optional ? '1' : '0').AppendLine();
    }
    File.WriteAllText(path, builder.ToString());
}

static void WriteAsyncV5PeerContextFixture(
    NegotiationManifest peer,
    string localSchemaHash,
    string path)
{
    var builder = new StringBuilder()
        .Append("localSchemaHash=").AppendLine(localSchemaHash)
        .Append("schemaHash=").AppendLine(peer.SchemaHash)
        .Append("abiMajor=").AppendLine(peer.AbiMajor.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Append("schemaVersion=").AppendLine(peer.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Append("contractMajor=").AppendLine(peer.ContractMajor.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Append("contractMinor=").AppendLine(peer.ContractMinor.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Append("featureBits=").AppendLine(peer.FeatureBits.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Append("requiredFeatureBits=").AppendLine(peer.RequiredFeatureBits.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Append("serviceID=").AppendLine(peer.ServiceId.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Append("methodID=").AppendLine(peer.MethodId.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Append("dtoID=").AppendLine(peer.DtoId.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Append("dtoVersion=").AppendLine(peer.DtoVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    foreach (var field in peer.RequestFields)
    {
        builder.Append("field=")
            .Append(field.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|')
            .Append(field.Type).Append('|')
            .Append(field.Nullable ? '1' : '0').Append('|')
            .Append(field.Optional ? '1' : '0').AppendLine();
    }
    File.WriteAllText(path, builder.ToString());
}

static void WriteAsyncV5ResultContextFixture(
    NegotiationManifest managedLocal,
    string nativePeerSchemaHash,
    string path)
{
    var builder = new StringBuilder()
        .Append("localSchemaHash=").AppendLine(managedLocal.SchemaHash)
        .Append("peerSchemaHash=").AppendLine(nativePeerSchemaHash)
        .AppendLine("profileId=1")
        .Append("abiMajor=").AppendLine(managedLocal.AbiMajor.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Append("schemaVersion=").AppendLine(managedLocal.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Append("contractMajor=").AppendLine(managedLocal.ContractMajor.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Append("contractMinor=").AppendLine(managedLocal.ContractMinor.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Append("featureBits=").AppendLine(managedLocal.FeatureBits.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Append("requiredFeatureBits=").AppendLine(managedLocal.RequiredFeatureBits.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Append("serviceID=").AppendLine(managedLocal.ServiceId.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Append("methodID=").AppendLine(managedLocal.MethodId.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Append("resultDTOID=").AppendLine(managedLocal.ResultDtoId.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .AppendLine("resultDTOVersion=1");
    foreach (var field in managedLocal.ResultFields)
    {
        builder.Append("field=")
            .Append(field.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('|')
            .Append(field.Type).Append('|')
            .Append(field.Nullable ? '1' : '0').Append('|')
            .Append(field.Optional ? '1' : '0').AppendLine();
    }
    File.WriteAllText(path, builder.ToString());
}

static byte[] Int32LittleEndian(int value)
{
    var bytes = new byte[sizeof(int)];
    BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
    return bytes;
}

static byte[] BuildNlf1Payload(params (int Id, int Kind, byte[] Payload)[] fields)
{
    using var body = new MemoryStream();
    Span<byte> header = stackalloc byte[7];
    foreach (var field in fields)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(header, checked((ushort)field.Id));
        header[2] = checked((byte)field.Kind);
        BinaryPrimitives.WriteUInt32LittleEndian(header[3..], checked((uint)field.Payload.Length));
        body.Write(header);
        body.Write(field.Payload);
    }
    var bodyBytes = body.ToArray();
    var payload = new byte[12 + bodyBytes.Length];
    payload[0] = 0x4e;
    payload[1] = 0x4c;
    payload[2] = 0x46;
    payload[3] = 0x31;
    payload[4] = 1;
    BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), checked((ushort)fields.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), checked((uint)bodyBytes.Length));
    bodyBytes.CopyTo(payload.AsSpan(12));
    return payload;
}

static byte[] EncodeGeneratedDto(
    Assembly assembly,
    string dtoTypeName,
    int dtoId,
    object?[] constructorArguments)
{
    var dtoType = assembly.GetType(dtoTypeName, throwOnError: true)!;
    var dto = Activator.CreateInstance(dtoType, constructorArguments)!;
    var codecType = assembly.GetType("NetLift.Generated.Codec", throwOnError: true)!;
    var arguments = new object?[] { dto, null };
    var status = codecType.GetMethod($"TryEncode_Dto_{dtoId}")!.Invoke(null, arguments);
    Require(status?.ToString() == "Success",
        $"Generated C# codec rejected Swift parity fixture DTO '{dtoTypeName}'.");
    return (byte[])arguments[1]!;
}

static string ReplaceCanonicalSchema(string generatedSource, Action<JsonObject> update)
{
    var schema = JsonNode.Parse(ReadSchema(generatedSource))!.AsObject();
    update(schema);
    schema["schemaHash"] = null;
    var serializerOptions = new JsonSerializerOptions { WriteIndented = false };
    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(schema.ToJsonString(serializerOptions)))).ToLowerInvariant();
    schema["schemaHash"] = hash;
    var marker = "public const string CanonicalJson = ";
    var assignmentStart = generatedSource.IndexOf(marker, StringComparison.Ordinal);
    Require(assignmentStart >= 0, "Generated source is missing CanonicalJson.");
    var literalStart = generatedSource.IndexOf('"', assignmentStart + marker.Length);
    var literalEnd = generatedSource.IndexOf("\";", literalStart, StringComparison.Ordinal);
    Require(literalStart >= 0 && literalEnd >= 0, "Generated CanonicalJson literal is malformed.");
    var literal = SyntaxFactory.Literal(schema.ToJsonString(serializerOptions)).ToString();
    return generatedSource[..literalStart] + literal + generatedSource[(literalEnd + 1)..];
}

static byte[] MutateManifest(byte[] manifestBytes, Action<JsonObject> update)
{
    var envelope = JsonNode.Parse(manifestBytes)!.AsObject();
    update(envelope["schema"]!.AsObject());
    using var schemaDocument = JsonDocument.Parse(envelope["schema"]!.ToJsonString());
    using var canonicalSchema = new MemoryStream();
    using (var writer = new Utf8JsonWriter(canonicalSchema, new JsonWriterOptions
           {
               Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
               Indented = false
           }))
        WriteCanonicalJson(schemaDocument.RootElement, writer);
    envelope["schemaHash"] = Fingerprint(Encoding.UTF8.GetString(canonicalSchema.ToArray()));
    return Encoding.UTF8.GetBytes(envelope.ToJsonString(new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    }));
}

static string ReadManifestHash(byte[] manifestBytes)
{
    using var document = JsonDocument.Parse(manifestBytes);
    return document.RootElement.GetProperty("schemaHash").GetString()!;
}

static void WriteCanonicalJson(JsonElement element, Utf8JsonWriter writer)
{
    switch (element.ValueKind)
    {
        case JsonValueKind.Object:
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject()
                         .OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonicalJson(property.Value, writer);
            }
            writer.WriteEndObject();
            break;
        case JsonValueKind.Array:
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray())
                WriteCanonicalJson(item, writer);
            writer.WriteEndArray();
            break;
        default:
            element.WriteTo(writer);
            break;
    }
}

static void RequireSwiftFlatCodecRejected(string generatedSource, string expectedMessage, string failureMessage)
{
    try
    {
        _ = ProjectionEmitter.Emit(generatedSource, new ProjectionOptions(EmitSwiftFlatCodecs: true));
    }
    catch (InvalidDataException exception) when (exception.Message.Contains(expectedMessage, StringComparison.Ordinal))
    {
        return;
    }
    throw new InvalidOperationException(failureMessage);
}

static string ReadSchema(string generatedSource)
{
    var start = generatedSource.IndexOf("public const string CanonicalJson = ", StringComparison.Ordinal);
    Require(start >= 0, "Generated source is missing CanonicalJson.");
    start = generatedSource.IndexOf('"', start);
    var end = generatedSource.IndexOf("\";", start, StringComparison.Ordinal);
    return JsonSerializer.Deserialize<string>(generatedSource[start..(end + 1)])!;
}

static MethodInfo GetInstanceRegisterMethod(Type hostType) =>
    hostType.GetMethods().Single(method =>
        method.Name == "Register" && !method.IsGenericMethod);

static LiftInvocationContext CreateAsyncCallbackV1ContextForTest(
    LiftAsyncCallbackV1HostDescriptor descriptor,
    ILiftNativeServiceInvoker nativeServices,
    CancellationToken cancellationToken,
    ulong correlationToken,
    ulong registrationToken,
    ulong generation,
    ulong operationToken) =>
    new(cancellationToken, correlationToken, nativeServices)
    {
        EndpointToken = descriptor.EndpointToken,
        RegistrationToken = registrationToken,
        RegistrationGeneration = generation,
        OperationToken = operationToken,
        PeerRequestSchema = descriptor.PeerRequestSchema,
        ManagedResultSchema = descriptor.ManagedResultSchema
    };

static string Fingerprint(string value) =>
    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void ExpectNegotiationException(Action action, NegotiationErrorCode expected, string name)
{
    try
    {
        action();
    }
    catch (NegotiationException exception) when (exception.Code == expected)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {expected} for {name}.");
}

static void ExpectManifestFailure(Action action, NegotiationErrorCode expected, string name)
{
    try
    {
        action();
    }
    catch (NegotiationException exception) when (exception.Code == expected)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {expected} for {name}.");
}
