using System.Text.Json;
using NetLift.Compiler;

if (args is ["compatibility", var previousPath, var currentPath])
{
    try
    {
        var previous = ContractCompiler.DeserializeLock(File.ReadAllText(previousPath));
        var current = ContractCompiler.DeserializeLock(File.ReadAllText(currentPath));
        var errors = ContractCompiler.Compare(previous, current);
        if (errors.Count == 0)
        {
            Console.WriteLine("Compatible.");
            return 0;
        }

        Console.Error.WriteLine("Incompatible:");
        foreach (var error in errors)
            Console.Error.WriteLine("  - " + error);
        return 1;
    }
    catch (Exception exception) when (exception is IOException or JsonException or ContractCompilerException)
    {
        Console.Error.WriteLine($"netlift: {exception.Message}");
        return 2;
    }
}

if (args.Length != 1)
{
    Console.Error.WriteLine(
        "Usage:" + Environment.NewLine +
        "  netlift <netlift.json>" + Environment.NewLine +
        "  netlift compatibility <previous-lock.json> <current-lock.json>");
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
    var nativeContractRoots = configuration.NativeContractRoots ?? [];
    var compilation = configuration.EmitAndroidBridge || configuration.EmitAppleBridge
        ? ContractCompiler.CompileWithImplementations(
            assemblyPath,
            configuration.ContractRoots,
            nativeContractRoots)
        : new ContractCompilation(
            ContractCompiler.Compile(
                assemblyPath,
                configuration.ContractRoots,
                nativeContractRoots),
            []);
    var manifest = compilation.Manifest;
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
    if (configuration.EmitAndroidBridge)
    {
        WriteIfChanged(
            Path.Combine(outputPath, "NetLiftAndroidBridge.g.cs"),
            ProjectionEmitter.EmitAndroidBridge(manifest, compilation.Implementations));
        WriteIfChanged(
            Path.Combine(outputPath, "NetLiftAndroidPlatform.kt"),
            ProjectionEmitter.EmitAndroidPlatformFacade(
                manifest,
                configuration.KotlinPackage));
    }
    if (configuration.EmitAppleBridge)
    {
        WriteIfChanged(
            Path.Combine(outputPath, "NetLiftAppleBridge.g.cs"),
            ProjectionEmitter.EmitAppleBridge(manifest, compilation.Implementations));
        WriteIfChanged(
            Path.Combine(outputPath, "NetLiftApplePlatform.swift"),
            ProjectionEmitter.EmitApplePlatformFacade(manifest));
    }

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
