using System.Text.Json;
using NetLift.Compiler;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: netlift <netlift.json>");
    return 2;
}

try
{
    var configurationPath = Path.GetFullPath(args[0]);
    var configurationDirectory = Path.GetDirectoryName(configurationPath)!;
    var configuration = JsonSerializer.Deserialize(
        File.ReadAllText(configurationPath),
        ContractJsonContext.Default.CompilerConfiguration)
        ?? throw new ContractCompilerException("Configuration is empty.");
    if (configuration.Version != 1)
        throw new ContractCompilerException($"Unsupported configuration version {configuration.Version}.");

    var assemblyPath = Path.GetFullPath(configuration.Assembly, configurationDirectory);
    var outputPath = Path.GetFullPath(configuration.Output, configurationDirectory);
    var manifest = ContractCompiler.Compile(assemblyPath, configuration.ContractRoots);
    var contractLock = ContractCompiler.CreateLock(manifest);
    if (configuration.CompatibilityLock is { Length: > 0 } lockPath)
    {
        var resolvedLockPath = Path.GetFullPath(lockPath, configurationDirectory);
        if (File.Exists(resolvedLockPath))
        {
            var previous = ContractCompiler.DeserializeLock(File.ReadAllText(resolvedLockPath));
            var compatibilityErrors = ContractCompiler.Compare(previous, contractLock);
            if (compatibilityErrors.Count != 0)
                throw new ContractCompilerException(
                    "Contract is incompatible:" + Environment.NewLine +
                    string.Join(Environment.NewLine, compatibilityErrors.Select(error => "  - " + error)));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(resolvedLockPath)!);
        WriteIfChanged(resolvedLockPath, ContractCompiler.Serialize(contractLock));
    }

    Directory.CreateDirectory(outputPath);
    WriteIfChanged(Path.Combine(outputPath, "netlift.manifest.json"), ContractCompiler.Serialize(manifest));
    foreach (var (name, content) in ProjectionEmitter.Emit(
                 manifest, configuration.KotlinPackage, configuration.SwiftModule))
        WriteIfChanged(Path.Combine(outputPath, name), content);

    Console.WriteLine($"Generated NetLift contract {manifest.SchemaHash} in {outputPath}");
    return 0;
}
catch (Exception exception) when (exception is IOException or JsonException or ContractCompilerException)
{
    Console.Error.WriteLine($"netlift: {exception.Message}");
    return 1;
}

static void WriteIfChanged(string path, string content)
{
    if (File.Exists(path) &&
        string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
        return;
    File.WriteAllText(path, content);
}
