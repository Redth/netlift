using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

if (args.Length != 4)
{
    Console.Error.WriteLine(
        "Usage: DeltaGenerator <baseline-source.cs> <updated-source.cs> <output-dir> <reference-dir>");
    return 2;
}

var baselineSourcePath = args[0];
var updatedSourcePath = args[1];
var outputPath = args[2];
var referencePath = args[3];
var outputDirectory = new DirectoryInfo(outputPath);
outputDirectory.Create();
var baselineBytesPath = Path.Combine(outputDirectory.FullName, "baseline");
Directory.CreateDirectory(baselineBytesPath);
var deltaDirectory = Path.Combine(outputDirectory.FullName, "delta");
Directory.CreateDirectory(deltaDirectory);

var references = Directory.GetFiles(referencePath, "*.dll")
    .Select(path => MetadataReference.CreateFromFile(path))
    .ToImmutableArray<MetadataReference>();
var compilationOptions = new CSharpCompilationOptions(
    OutputKind.DynamicallyLinkedLibrary,
    optimizationLevel: OptimizationLevel.Debug,
    deterministic: true,
    nullableContextOptions: NullableContextOptions.Enable);
var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
var baselineTree = CSharpSyntaxTree.ParseText(
    SourceText.From(File.ReadAllText(baselineSourcePath), Encoding.UTF8),
    parseOptions,
    baselineSourcePath);
var updatedTree = CSharpSyntaxTree.ParseText(
    SourceText.From(File.ReadAllText(updatedSourcePath), Encoding.UTF8),
    parseOptions,
    updatedSourcePath);
var baselineCompilation = CSharpCompilation.Create(
    "NetLift.HotReloadTarget",
    new[] { baselineTree },
    references,
    compilationOptions);
var updatedCompilation = baselineCompilation.ReplaceSyntaxTree(baselineTree, updatedTree);

using var baselinePe = new MemoryStream();
using var baselinePdb = new MemoryStream();
var baselineEmit = baselineCompilation.Emit(
    baselinePe,
    pdbStream: baselinePdb,
    options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
if (!baselineEmit.Success)
{
    PrintDiagnostics(baselineEmit.Diagnostics);
    return 3;
}

var baselineAssemblyPath = Path.Combine(baselineBytesPath, "NetLift.HotReloadTarget.dll");
var baselinePdbPath = Path.Combine(baselineBytesPath, "NetLift.HotReloadTarget.pdb");
File.WriteAllBytes(baselineAssemblyPath, baselinePe.ToArray());
File.WriteAllBytes(baselinePdbPath, baselinePdb.ToArray());

var oldMethod = GetObserveMethod(baselineCompilation);
var newMethod = GetObserveMethod(updatedCompilation);
var baselineNodes = baselineTree.GetRoot()
    .DescendantNodesAndSelf()
    .GroupBy(node => (node.RawKind, node.Span))
    .ToDictionary(group => group.Key, group => group.First());
Func<SyntaxNode, SyntaxNode> syntaxMap = node =>
    baselineNodes.TryGetValue((node.RawKind, node.Span), out var oldNode)
        ? oldNode
        : throw new InvalidDataException(
            $"Could not map updated syntax node {node.Kind()} at {node.Span} to baseline.");
using var module = ModuleMetadata.CreateFromImage(ImmutableArray.Create(baselinePe.ToArray()));
var baseline = EmitBaseline.CreateInitialBaseline(
    baselineCompilation,
    module,
    _ => default,
    _ => default(StandaloneSignatureHandle),
    hasPortableDebugInformation: true);
using var metadataDelta = new MemoryStream();
using var ilDelta = new MemoryStream();
using var pdbDelta = new MemoryStream();
var difference = updatedCompilation.EmitDifference(
    baseline,
    new[]
    {
        new SemanticEdit(
            SemanticEditKind.Update,
            oldMethod,
            newMethod,
            syntaxMap,
            runtimeRudeEdit: static _ => null,
            instrumentation: default)
    },
    isAddedSymbol: static _ => false,
    metadataDelta,
    ilDelta,
    pdbDelta,
    EmitDifferenceOptions.Default,
    CancellationToken.None);
if (!difference.Success)
{
    PrintDiagnostics(difference.Diagnostics);
    return 4;
}
if (difference.UpdatedMethods.Length != 1)
{
    Console.Error.WriteLine(
        $"Expected one changed method; Roslyn reported {difference.UpdatedMethods.Length}.");
    return 5;
}

WriteDelta(deltaDirectory, "metadata.delta", metadataDelta);
WriteDelta(deltaDirectory, "il.delta", ilDelta);
WriteDelta(deltaDirectory, "pdb.delta", pdbDelta);
var methodToken = MetadataTokens.GetToken(difference.UpdatedMethods[0]);
var manifest = Path.Combine(outputDirectory.FullName, "delta-manifest.txt");
File.WriteAllText(
    manifest,
    $"assembly=NetLift.HotReloadTarget{Environment.NewLine}" +
    $"methodToken=0x{methodToken:x8}{Environment.NewLine}" +
    $"metadataSha256={Hash(metadataDelta.ToArray())}{Environment.NewLine}" +
    $"ilSha256={Hash(ilDelta.ToArray())}{Environment.NewLine}" +
    $"pdbSha256={Hash(pdbDelta.ToArray())}{Environment.NewLine}");
Console.WriteLine(
    $"DELTA-GENERATED: method=Probe.Observe token=0x{methodToken:x8} " +
    $"metadata={metadataDelta.Length} il={ilDelta.Length} pdb={pdbDelta.Length}");
Console.WriteLine($"BASELINE-ASSEMBLY: {baselineAssemblyPath}");
return 0;

static IMethodSymbol GetObserveMethod(CSharpCompilation compilation)
{
    var probe = compilation.GetTypeByMetadataName("NetLift.HotReloadTarget.Probe")
        ?? throw new InvalidDataException("Source does not declare NetLift.HotReloadTarget.Probe.");
    return probe.GetMembers("Observe").OfType<IMethodSymbol>().Single();
}

static void WriteDelta(string directory, string name, MemoryStream stream)
{
    File.WriteAllBytes(Path.Combine(directory, name), stream.ToArray());
}

static void PrintDiagnostics(IEnumerable<Diagnostic> diagnostics)
{
    foreach (var diagnostic in diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error))
        Console.Error.WriteLine(diagnostic);
}

static string Hash(byte[] bytes) =>
    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
