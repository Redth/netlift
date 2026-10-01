using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetLift.SchemaDiff;

var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
var baseline = Load("baseline.json");
var unchanged = SchemaDiffEngine.Compare(baseline, baseline);
var baselineHash = Hash(baseline);
AssertEqual(Expected(
    "compatible", baselineHash, baselineHash, 0, 0, 0, ""),
    SchemaDiffEngine.ToJson(unchanged), "unchanged schema exact report");

var compatibleFixture = Load("compatible-additions.json");
var compatible = SchemaDiffEngine.Compare(baseline, compatibleFixture);
var compatibleHash = Hash(compatibleFixture);
var compatibleExpected = Expected(
    "compatible", baselineHash, compatibleHash, 2, 0, 0,
    """
      {
        "classification": "compatible",
        "code": "field-added",
        "path": "dtos[id=2].fields[id=3]",
        "message": "Field added; additive within the same major version."
      },
      {
        "classification": "compatible",
        "code": "method-added",
        "path": "services[id=1].methods[id=2]",
        "message": "Method added; additive within the same major version."
      }
    """);
AssertEqual(compatibleExpected, SchemaDiffEngine.ToJson(compatible), "compatible additions exact report");
AssertEqual(compatibleExpected, SchemaDiffEngine.ToJson(
    SchemaDiffEngine.Compare(baseline, compatibleFixture)), "deterministic repeat output");

var breakingFixture = Load("breaking-changes.json");
var breaking = SchemaDiffEngine.Compare(baseline, breakingFixture);
AssertEqual(Expected(
    "breaking", baselineHash, Hash(breakingFixture), 0, 3, 0,
    """
      {
        "classification": "breaking",
        "code": "type-kind-changed",
        "path": "dtos[id=2].fields[id=1].type",
        "message": "Wire type kind changed."
      },
      {
        "classification": "breaking",
        "code": "nullability-changed",
        "path": "dtos[id=2].fields[id=2].type",
        "message": "Type nullability changed."
      },
      {
        "classification": "breaking",
        "code": "method-removed",
        "path": "services[id=1].methods[id=1]",
        "message": "method was removed."
      }
    """),
    SchemaDiffEngine.ToJson(breaking), "breaking changes exact report");

var requiredField = Load("required-field-added.json");
AssertClassified("breaking", "required-field-added", baseline, requiredField);
var renumberedField = Load("renumbered-field.json");
AssertClassified("breaking", "field-id-changed", baseline, renumberedField);
var explicitOptionalField = Load("compatible-explicit-optional.json");
AssertClassified("compatible", "field-added", baseline, explicitOptionalField);

var unsupportedVersion = Load("unsupported-evolution.json");
var unsupportedReport = SchemaDiffEngine.Compare(baseline, unsupportedVersion);
AssertEqual(Expected(
    "unsupported", baselineHash, Hash(unsupportedVersion), 0, 0, 1,
    """
      {
        "classification": "unsupported",
        "code": "schema-version-unsupported",
        "path": "$.schemaVersion",
        "message": "Only canonical schema version 2 is supported."
      }
    """),
    SchemaDiffEngine.ToJson(unsupportedReport), "unsupported schema version exact report");

var unknownProperty = Load("unknown-property.json");
AssertClassified("unsupported", "schema-property-unsupported", baseline, unknownProperty);
var unsupportedType = Load("unsupported-type.json");
AssertClassified("unsupported", "type-kind-unsupported", unsupportedType, unsupportedType);

var cliDirectory = Path.Combine(AppContext.BaseDirectory, "schema-diff-cli-test");
Directory.CreateDirectory(cliDirectory);
try
{
    var beforePath = Path.Combine(cliDirectory, "before.json");
    var afterPath = Path.Combine(cliDirectory, "after.json");
    File.WriteAllText(beforePath, baseline);
    File.WriteAllText(afterPath, compatibleFixture);
    var output = new StringWriter();
    var error = new StringWriter();
    var exitCode = SchemaDiffCommand.Run(new[] { beforePath, afterPath }, output, error);
    AssertEqual(0, exitCode, "compatible CLI exit code: " + error);
    AssertEqual(compatibleExpected + Environment.NewLine, output.ToString(), "CLI JSON output");
    AssertEqual("", error.ToString(), "CLI stderr");

    var generatedBeforePath = Path.Combine(cliDirectory, "before.g.cs");
    var generatedAfterPath = Path.Combine(cliDirectory, "after.g.cs");
    File.WriteAllText(generatedBeforePath, GeneratedSource(baseline));
    File.WriteAllText(generatedAfterPath, GeneratedSource(compatibleFixture));
    output = new StringWriter();
    error = new StringWriter();
    exitCode = SchemaDiffCommand.Run(new[] { generatedBeforePath, generatedAfterPath }, output, error);
    AssertEqual(0, exitCode, "generated C# CLI exit code");
    AssertEqual(compatibleExpected + Environment.NewLine, output.ToString(), "generated C# CLI output");
    AssertEqual("", error.ToString(), "generated C# CLI stderr");

    File.WriteAllText(afterPath, breakingFixture);
    output = new StringWriter();
    error = new StringWriter();
    exitCode = SchemaDiffCommand.Run(new[] { beforePath, afterPath }, output, error);
    AssertEqual(1, exitCode, "breaking CLI exit code");
    AssertEqual(SchemaDiffEngine.ToJson(breaking) + Environment.NewLine, output.ToString(),
        "breaking CLI output");
    AssertEqual("", error.ToString(), "breaking CLI stderr");

    File.WriteAllText(afterPath, unsupportedVersion);
    output = new StringWriter();
    error = new StringWriter();
    exitCode = SchemaDiffCommand.Run(new[] { beforePath, afterPath }, output, error);
    AssertEqual(2, exitCode, "unsupported CLI exit code");
    AssertEqual(SchemaDiffEngine.ToJson(unsupportedReport) + Environment.NewLine, output.ToString(),
        "unsupported CLI output");
    AssertEqual("", error.ToString(), "unsupported CLI stderr");

    output = new StringWriter();
    error = new StringWriter();
    exitCode = SchemaDiffCommand.Run(Array.Empty<string>(), output, error);
    AssertEqual(64, exitCode, "CLI usage exit code");
    AssertEqual("Usage: NetLift.SchemaDiff <before-schema.json> <after-schema.json>" + Environment.NewLine,
        error.ToString(), "CLI usage diagnostic");
}
finally
{
    Directory.Delete(cliDirectory, recursive: true);
}

var invalidHash = JsonNode.Parse(baseline)!.AsObject();
invalidHash["schemaHash"] = new string('0', 64);
try
{
    SchemaDiffEngine.Compare(invalidHash.ToJsonString(), baseline);
    throw new InvalidOperationException("Invalid schema hash was accepted.");
}
catch (SchemaDiffInputException exception) when (exception.Message.Contains("schema hash is invalid", StringComparison.Ordinal))
{
}

Console.WriteLine("PASS: schema diff classifications, deterministic exact reports, CLI and canonical hash validation.");

string Load(string fileName)
{
    var json = File.ReadAllText(Path.Combine(fixtures, fileName));
    var root = JsonNode.Parse(json)!.AsObject();
    root["schemaHash"] = null;
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToJsonString())))
        .ToLowerInvariant();
    root["schemaHash"] = hash;
    return root.ToJsonString();
}

static string Hash(string json) =>
    JsonNode.Parse(json)!["schemaHash"]!.GetValue<string>();

static string GeneratedSource(string schemaJson) =>
    "public static class Contract { public const string CanonicalJson = " +
    JsonSerializer.Serialize(schemaJson) + "; }";

static string Expected(
    string classification,
    string beforeHash,
    string afterHash,
    int compatible,
    int breaking,
    int unsupported,
    string changes)
{
    var changeSection = string.IsNullOrEmpty(changes)
        ? "  \"changes\": []"
        : "  \"changes\": [\n" +
          string.Join("\n", changes.Trim('\r', '\n').Split('\n').Select(line => "  " + line)) +
          "\n  ]";
    return $$"""
        {
          "formatVersion": 1,
          "classification": "{{classification}}",
          "beforeSchemaHash": "{{beforeHash}}",
          "afterSchemaHash": "{{afterHash}}",
          "summary": {
            "compatible": {{compatible}},
            "breaking": {{breaking}},
            "unsupported": {{unsupported}}
          },
        {{changeSection}}
        }
        """.Replace("\r\n", "\n", StringComparison.Ordinal);
}

static void AssertClassified(string classification, string code, string before, string after)
{
    var report = SchemaDiffEngine.Compare(before, after);
    if (report.Classification != classification || !report.Changes.Any(change => change.Code == code))
        throw new InvalidOperationException(
            $"Expected {classification}/{code}, got {report.Classification} [{string.Join(", ", report.Changes.Select(change => change.Code))}].");
}

static void AssertEqual<T>(T expected, T actual, string description)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException(
            $"{description} mismatch.{Environment.NewLine}Expected:{Environment.NewLine}{expected}{Environment.NewLine}Actual:{Environment.NewLine}{actual}");
}
