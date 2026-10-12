using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;

namespace NetLift.MauiEmbedding;

public sealed record EmbeddingRequest(string Name);
public sealed record EmbeddingResponse(
    string Message, string Platform, string DevicePlatform,
    string DeviceVersion, string DeviceModel, string DeviceManufacturer);
public sealed record StorageKey(string Key);
public sealed record StorageEntry(string Key, string Value);
public sealed record ClipboardRequest(string? Text);
public sealed record TextValue(string? Value);
public sealed record OperationResult(bool Completed);

public interface IEmbeddingService
{
    Task<EmbeddingResponse> GreetAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default);

    Task<TextValue> GetSecureValueAsync(StorageKey request, CancellationToken cancellationToken = default);
    Task<OperationResult> SetSecureValueAsync(StorageEntry request, CancellationToken cancellationToken = default);
    Task<OperationResult> RemoveSecureValueAsync(StorageKey request, CancellationToken cancellationToken = default);
    Task<TextValue> ClipboardAsync(ClipboardRequest request, CancellationToken cancellationToken = default);
}

public sealed class EmbeddingService : IEmbeddingService
{
    public Task<EmbeddingResponse> GreetAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
#if IOS
        const string platform = "net11.0-ios";
#elif ANDROID
        const string platform = "net11.0-android";
#else
#error This sample must be built with a mobile target framework.
#endif
        var device = DeviceInfo.Current;
        return Task.FromResult(new EmbeddingResponse(
            $"Hello {request.Name} from the MAUI library", platform,
            device.Platform.ToString(), device.VersionString, device.Model, device.Manufacturer));
    }

    public async Task<TextValue> GetSecureValueAsync(
        StorageKey request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Key);
        cancellationToken.ThrowIfCancellationRequested();
        return new TextValue(await SecureStorage.Default.GetAsync(request.Key));
    }

    public async Task<OperationResult> SetSecureValueAsync(StorageEntry request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Key);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Value);
        cancellationToken.ThrowIfCancellationRequested();
        await SecureStorage.Default.SetAsync(request.Key, request.Value);
        return new OperationResult(true);
    }

    public Task<OperationResult> RemoveSecureValueAsync(StorageKey request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Key);
        cancellationToken.ThrowIfCancellationRequested();
        SecureStorage.Default.Remove(request.Key);
        return Task.FromResult(new OperationResult(true));
    }

    public async Task<TextValue> ClipboardAsync(
        ClipboardRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return new TextValue(await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            if (request.Text is not null)
                await Clipboard.Default.SetTextAsync(request.Text);
            return await Clipboard.Default.GetTextAsync();
        }));
    }
}
