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
Require(first.Services[0].Methods.Count == 1, "Expected one method.");
Require(first.Services[0].Methods[0].IsAsync, "Expected an async method.");
Require(first.Services[0].Methods[0].SupportsCancellation, "Expected cancellation support.");
Require(first.Types.Count == 3, "Expected two DTOs and one enum.");

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
        $"const val SubmitOrderAsyncId: UInt = {first.Services[0].Methods[0].Id}u",
        StringComparison.Ordinal),
    "Generated Kotlin client omitted its public member ID.");

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

Console.WriteLine($"Compiler tests passed for schema {first.SchemaHash}.");
return;

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
