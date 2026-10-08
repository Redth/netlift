using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetLift.Compiler;

if (!TryReadArguments(
        args,
        out var configPath,
        out var packageRoot,
        out var outputPath,
        out var workPath))
{
    Console.Error.WriteLine(
        "Usage: netlift-swift generate --config <netlift.json> " +
        "--package-root <path> --output <path> --work <path>");
    return 2;
}

try
{
    configPath = Path.GetFullPath(configPath);
    packageRoot = Path.GetFullPath(packageRoot);
    outputPath = Path.GetFullPath(outputPath);
    workPath = Path.GetFullPath(workPath);
    RequireWithin(packageRoot, configPath, "Configuration");

    var options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    var configuration = JsonSerializer.Deserialize<SwiftPluginConfiguration>(
        File.ReadAllText(configPath),
        options) ?? throw new SwiftDriverException("Configuration is empty.");
    if (configuration.Version != 1)
        throw new SwiftDriverException(
            $"Unsupported configuration version {configuration.Version}.");
    if (configuration.ContractRoots is not { Count: > 0 })
        throw new SwiftDriverException("contractRoots must contain at least one service.");
    if (string.IsNullOrWhiteSpace(configuration.SwiftModule))
        throw new SwiftDriverException("swiftModule is required.");

    var configDirectory = Path.GetDirectoryName(configPath)!;
    var managedProject = Path.GetFullPath(
        configuration.ManagedProject,
        configDirectory);
    RequireWithin(packageRoot, managedProject, "Managed project");
    if (!File.Exists(managedProject))
        throw new SwiftDriverException(
            $"Managed project does not exist: {managedProject}");

    var managedOutput = Path.Combine(workPath, "managed");
    var baseOutputPath = Path.Combine(managedOutput, "bin") +
        Path.DirectorySeparatorChar;
    var baseIntermediateOutputPath = Path.Combine(managedOutput, "obj") +
        Path.DirectorySeparatorChar;
    var dotnet = ResolveDotnet();
    Run(
        dotnet,
        [
            "build",
            managedProject,
            "-c",
            configuration.Configuration,
            "-f",
            configuration.ManagedFramework,
            $"-p:BaseOutputPath={baseOutputPath}",
            $"-p:BaseIntermediateOutputPath={baseIntermediateOutputPath}",
            "-p:DefaultItemExcludesInProjectFolder=obj/**",
            "-v:minimal",
        ]);
    var managedAssembly = Run(
        dotnet,
        [
            "msbuild",
            managedProject,
            "-getProperty:TargetPath",
            $"-p:Configuration={configuration.Configuration}",
            $"-p:TargetFramework={configuration.ManagedFramework}",
            $"-p:BaseOutputPath={baseOutputPath}",
            $"-p:BaseIntermediateOutputPath={baseIntermediateOutputPath}",
            "-nologo",
        ]).Trim();
    if (!Path.IsPathFullyQualified(managedAssembly) || !File.Exists(managedAssembly))
        throw new SwiftDriverException(
            $"Managed build did not report a valid TargetPath: {managedAssembly}");

    var manifest = ContractCompiler.Compile(
        managedAssembly,
        configuration.ContractRoots);
    var generated = ProjectionEmitter.Emit(
        manifest,
        "dev.netlift.generated",
        configuration.SwiftModule);
    Directory.CreateDirectory(outputPath);
    WriteIfChanged(
        Path.Combine(outputPath, "NetLiftContracts.swift"),
        generated["NetLiftContracts.swift"]);
    WriteIfChanged(
        Path.Combine(outputPath, "netlift.manifest.json"),
        ContractCompiler.Serialize(manifest));
    Console.WriteLine(
        $"Generated Swift contract {manifest.SchemaHash} in {outputPath}");
    return 0;
}
catch (Exception exception) when (
    exception is IOException
        or JsonException
        or ContractCompilerException
        or SwiftDriverException)
{
    Console.Error.WriteLine($"netlift-swift: {exception.Message}");
    return 1;
}

static bool TryReadArguments(
    string[] args,
    out string config,
    out string packageRoot,
    out string output,
    out string work)
{
    config = "";
    packageRoot = "";
    output = "";
    work = "";
    if (args.Length != 9 || args[0] != "generate")
        return false;
    for (var index = 1; index < args.Length; index += 2)
    {
        switch (args[index])
        {
            case "--config":
                config = args[index + 1];
                break;
            case "--package-root":
                packageRoot = args[index + 1];
                break;
            case "--output":
                output = args[index + 1];
                break;
            case "--work":
                work = args[index + 1];
                break;
            default:
                return false;
        }
    }
    return config.Length > 0 &&
        packageRoot.Length > 0 &&
        output.Length > 0 &&
        work.Length > 0;
}

static void RequireWithin(string root, string path, string description)
{
    var relative = Path.GetRelativePath(root, path);
    if (relative == ".." ||
        relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
        Path.IsPathFullyQualified(relative))
        throw new SwiftDriverException(
            $"{description} must be inside the Swift package: {path}");
}

static string Run(string fileName, IReadOnlyList<string> arguments)
{
    var startInfo = new ProcessStartInfo(fileName)
    {
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false,
    };
    foreach (var argument in arguments)
        startInfo.ArgumentList.Add(argument);
    Process? process;
    try
    {
        process = Process.Start(startInfo);
    }
    catch (Exception exception)
    {
        throw new SwiftDriverException(
            $"Could not start {fileName}: {exception.Message}");
    }
    using var runningProcess = process
        ?? throw new SwiftDriverException($"Could not start {fileName}.");
    var standardOutput = runningProcess.StandardOutput.ReadToEnd();
    var standardError = runningProcess.StandardError.ReadToEnd();
    runningProcess.WaitForExit();
    if (runningProcess.ExitCode != 0)
        throw new SwiftDriverException(
            $"{fileName} failed with exit code {runningProcess.ExitCode}:{Environment.NewLine}" +
            standardError + standardOutput);
    return standardOutput;
}

static string ResolveDotnet()
{
    var candidates = new List<string>();
    foreach (var variable in new[] { "DOTNET_HOST_PATH", "DOTNET_ROOT" })
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value))
            continue;
        candidates.Add(
            Directory.Exists(value) ? Path.Combine(value, "dotnet") : value);
    }
    var path = Environment.GetEnvironmentVariable("PATH");
    if (!string.IsNullOrWhiteSpace(path))
    {
        candidates.AddRange(path.Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, "dotnet")));
    }
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    candidates.Add(Path.Combine(
        home,
        "Library",
        "Application Support",
        "dotnetup",
        "dotnet",
        "dotnet"));
    candidates.Add("/usr/local/share/dotnet/dotnet");
    var resolved = candidates.FirstOrDefault(File.Exists);
    return resolved ?? throw new SwiftDriverException(
        "The .NET SDK host was not found. Install .NET or set DOTNET_ROOT.");
}

static void WriteIfChanged(string path, string content)
{
    if (File.Exists(path) &&
        string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
        return;
    File.WriteAllText(path, content);
}

internal sealed record SwiftPluginConfiguration(
    int Version,
    string ManagedProject,
    IReadOnlyList<string> ContractRoots,
    string SwiftModule,
    string ManagedFramework = "net10.0",
    string Configuration = "Release");

internal sealed class SwiftDriverException(string message) : Exception(message);
