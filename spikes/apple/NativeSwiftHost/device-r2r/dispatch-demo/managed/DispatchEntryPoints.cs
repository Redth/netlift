using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using NetLift;
using NetLift.Generated;

namespace NetLift.DispatchDemo;

public enum DispatchDestination : int
{
    North = 1,
    Central = 2,
    South = 3,
}

[LiftDto("dispatch-command", Id = 1, Version = 1)]
public sealed record DispatchCommand(
    [property: LiftField(1)] int PackageCount,
    [property: LiftField(2)] int Destination);

[LiftDto("dispatch-receipt", Id = 2, Version = 1)]
public sealed record DispatchReceipt(
    [property: LiftField(1)] long ReceiptId,
    [property: LiftField(2)] int PackageCount,
    [property: LiftField(3)] int Destination,
    [property: LiftField(4)] string TrackingCode);

internal sealed record DispatchRequest(int PackageCount, DispatchDestination Destination);

public static class DispatchEntryPoints
{
    private static readonly ConcurrentDictionary<long, TaskCompletionSource<bool>>
        PendingConfirmations = new();
    private static long nextConfirmationId = 10_000;
    private static long nextReceiptId;

    [DllImport("__Internal", EntryPoint = "netlift_cart_request_confirmation")]
    private static extern int RequestNativeConfirmation(
        long requestId,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string title);

    [DllImport("__Internal", EntryPoint = "netlift_cart_publish_result")]
    private static extern void PublishNativeResult(
        long operationId,
        uint status,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] byte[] result,
        uint resultLength,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 5)] byte[] error,
        uint errorLength);

    public static unsafe int StartDispatch(
        IntPtr commandBytes,
        uint commandLength,
        long operationId)
    {
        if (operationId <= 0)
            return 1;

        if (commandBytes == IntPtr.Zero || commandLength > Codec.MaxBufferBytes)
            return 2;

        var payload = new ReadOnlySpan<byte>((void *)commandBytes, checked((int)commandLength)).ToArray();
        if (Codec.TryDecode_Dto_1(payload, out var command) != CodecStatus.Success ||
            command is null)
            return 2;
        if (command.PackageCount is < 1 or > 100 ||
            !Enum.IsDefined((DispatchDestination)command.Destination))
            return 1;

        var request = new DispatchRequest(
            command.PackageCount,
            (DispatchDestination)command.Destination);
        _ = Task.Run(() => DispatchAsync(request, operationId));
        return 0;
    }

    public static int CompleteConfirmation(long requestId, int accepted) =>
        PendingConfirmations.TryGetValue(requestId, out var completion) &&
        completion.TrySetResult(accepted != 0)
            ? 0
            : 10;

    private static async Task DispatchAsync(DispatchRequest command, long operationId)
    {
        var requestId = Interlocked.Increment(ref nextConfirmationId);
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!PendingConfirmations.TryAdd(requestId, completion))
        {
            PublishFailure(operationId, "Dispatch confirmation could not be registered.");
            return;
        }

        try
        {
            var destination = command.Destination.ToString();
            var title = $"Dispatch {command.PackageCount} packages to {destination}?";
            if (RequestNativeConfirmation(requestId, title) != 1)
                throw new InvalidOperationException("Native dispatch confirmation is unavailable.");

            if (!await completion.Task.ConfigureAwait(false))
            {
                PublishFailure(operationId, "Dispatch was declined.");
                return;
            }

            var receiptId = Interlocked.Increment(ref nextReceiptId);
            var receipt = new DispatchReceipt(
                receiptId,
                command.PackageCount,
                (int)command.Destination,
                $"DSP-{destination.ToUpperInvariant()}-{receiptId:D6}");
            var codecStatus = Codec.TryEncode_Dto_2(receipt, out var result);
            if (codecStatus != CodecStatus.Success)
                throw new InvalidOperationException(
                    $"Generated dispatch receipt codec failed ({codecStatus}).");
            PublishNativeResult(
                operationId,
                0,
                result,
                (uint)result.Length,
                [],
                0);
        }
        catch (Exception exception)
        {
            PublishFailure(operationId, exception.Message);
        }
        finally
        {
            PendingConfirmations.TryRemove(requestId, out _);
        }
    }

    private static void PublishFailure(long operationId, string message)
    {
        var diagnostic = Encoding.UTF8.GetBytes(message);
        if (diagnostic.Length > Codec.MaxBufferBytes)
            diagnostic = Encoding.UTF8.GetBytes("Dispatch failure diagnostic exceeded 4096 bytes.");
        PublishNativeResult(operationId, 8, [], 0, diagnostic, (uint)diagnostic.Length);
    }
}
