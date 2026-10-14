namespace NetLift.Compiler.Tests.Fixtures;

public sealed record ConfirmationRequest(string OrderId);

public sealed record ConfirmationResult(bool Accepted);

public interface IManagedServiceFixture
{
    Task<ConfirmationResult> SubmitAsync(
        ConfirmationRequest request,
        IConfirmationHandler confirmationHandler,
        CancellationToken cancellationToken = default);
}

public sealed class ManagedServiceFixture : IManagedServiceFixture
{
    public Task<ConfirmationResult> SubmitAsync(
        ConfirmationRequest request,
        IConfirmationHandler confirmationHandler,
        CancellationToken cancellationToken = default) =>
        confirmationHandler.ConfirmAsync(request, cancellationToken);
}

public interface IConfirmationHandler
{
    Task<ConfirmationResult> ConfirmAsync(
        ConfirmationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IStreamingNativeService
{
    IAsyncEnumerable<ConfirmationResult> ConfirmationsAsync(
        ConfirmationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IStreamingManagedServiceFixture
{
    IAsyncEnumerable<ConfirmationResult> WatchAsync(
        ConfirmationRequest request,
        IConfirmationHandler confirmationHandler);
}

public interface IRecursiveNativeServiceFixture
{
    Task<ConfirmationResult> SubmitAsync(
        ConfirmationRequest request,
        IConfirmationHandler confirmationHandler);
}

public sealed record AuditResult(bool Accepted);

public interface IAuditHandler
{
    Task<AuditResult> AuditAsync(
        ConfirmationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IMultiCallbackServiceFixture
{
    Task<ConfirmationResult> SubmitAsync(
        ConfirmationRequest request,
        IConfirmationHandler confirmationHandler,
        IAuditHandler auditHandler,
        CancellationToken cancellationToken = default);
}

public sealed class MultiCallbackServiceFixture : IMultiCallbackServiceFixture
{
    public async Task<ConfirmationResult> SubmitAsync(
        ConfirmationRequest request,
        IConfirmationHandler confirmationHandler,
        IAuditHandler auditHandler,
        CancellationToken cancellationToken = default)
    {
        var confirmation = await confirmationHandler.ConfirmAsync(request, cancellationToken);
        var audit = await auditHandler.AuditAsync(request, cancellationToken);
        return new ConfirmationResult(confirmation.Accepted && audit.Accepted);
    }
}
