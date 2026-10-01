using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using NetLift.Generated;
using BusinessOrderDraft = NetLift.Samples.Orders.OrderDraft;
using BusinessOrderUpdate = NetLift.Samples.Orders.OrderUpdate;
using BusinessOrdersService = NetLift.Samples.Orders.OrdersService;
using Contracts = NetLift.ManagedBridge.Contracts;

namespace NetLift.ManagedBridge;

public static class ManagedEntryPoints
{
    private static readonly ConcurrentDictionary<long, CancellationTokenSource> ActiveOrders = new();
    private static readonly ConcurrentDictionary<long, TaskCompletionSource<bool>> PendingConfirmations = new();
    private static readonly BusinessOrdersService BusinessOrders =
        new(new NativeConfirmation());
    private static readonly Contracts.IOrdersService Orders =
        new BusinessOrdersAdapter(BusinessOrders);
    private static long nextConfirmationId;

    public static IAsyncEnumerable<BusinessOrderUpdate> WatchOrderUpdates(
        CancellationToken cancellationToken) =>
        BusinessOrders.WatchAsync(cancellationToken);

    public static int RunOrderUpdateOverflowProbe(int updateCount)
    {
        if (updateCount <= 0 || updateCount > 33)
            return 1;

        try
        {
            for (var index = 1; index <= updateCount; index++)
            {
                BusinessOrders.PlaceAsync(
                    new BusinessOrderDraft($"STREAM-OVERFLOW-{index}", 1),
                    CancellationToken.None).GetAwaiter().GetResult();
            }

            Console.WriteLine($"Orders.Business overflow probe emitted {updateCount} updates.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Orders.Business overflow probe failed: {exception.Message}");
            return 8;
        }
    }

    [DllImport("__Internal", EntryPoint = "netlift_request_confirmation")]
    private static extern int RequestNativeConfirmation(
        long requestId,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string title);

    [DllImport("__Internal", EntryPoint = "netlift_publish_result")]
    private static extern void PublishNativeResult(
        long operationToken,
        uint status,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] byte[] result,
        uint resultLength,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 5)] byte[] error,
        uint errorLength);

    public static int StartOrder(
        IntPtr skuBytes,
        uint skuLength,
        int quantity,
        long operationToken)
    {
        if (skuLength == 0 || skuLength > Codec.MaxBufferBytes || skuBytes == IntPtr.Zero ||
            quantity <= 0 || operationToken <= 0)
        {
            return 1;
        }

        var sku = Marshal.PtrToStringUTF8(skuBytes, checked((int)skuLength));

        if (string.IsNullOrWhiteSpace(sku))
            return 1;

        var cancellation = new CancellationTokenSource();
        if (!ActiveOrders.TryAdd(operationToken, cancellation))
        {
            cancellation.Dispose();
            return 1;
        }

        try
        {
            var worker = Task.Run(() => PlaceOrderAsync(
                operationToken,
                sku,
                quantity,
                cancellation));
            _ = worker.ContinueWith(
                static completed =>
                {
                    var failure = completed.Exception!.GetBaseException();
                    Console.Error.WriteLine(
                        $"Managed order worker terminated unexpectedly ({failure.GetType().Name}).");
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return 0;
        }
        catch (Exception)
        {
            ActiveOrders.TryRemove(operationToken, out _);
            cancellation.Dispose();
            return 8;
        }
    }

    public static int CancelOrder(long operationToken)
    {
        if (!ActiveOrders.TryGetValue(operationToken, out var cancellation))
            return 10;

        try
        {
            cancellation.Cancel();
            return 0;
        }
        catch (ObjectDisposedException)
        {
            return 10;
        }
    }

    public static int CompleteConfirmation(long requestId, int accepted)
    {
        return PendingConfirmations.TryGetValue(requestId, out var completion) &&
            completion.TrySetResult(accepted != 0)
                ? 0
                : 10;
    }

    private static async Task PlaceOrderAsync(
        long operationToken,
        string sku,
        int quantity,
        CancellationTokenSource cancellation)
    {
        try
        {
            var draft = new Contracts.OrderDraft(sku, quantity);
            var receipt = await Dispatch.Dispatch_2_1(
                Orders,
                draft,
                cancellation.Token).ConfigureAwait(false);
            if (Codec.TryEncode_Dto_4(receipt, out var result) != CodecStatus.Success)
            {
                PublishFailure(operationToken, 8, "Managed order returned an invalid receipt.");
                return;
            }

            PublishNativeResult(operationToken, 0, result, (uint)result.Length, [], 0);
        }
        catch (OperationCanceledException)
        {
            PublishNativeResult(operationToken, 12, [], 0, [], 0);
        }
        catch (Exception exception)
        {
            PublishFailure(operationToken, 8, exception.Message);
        }
        finally
        {
            if (ActiveOrders.TryRemove(operationToken, out var removed))
                removed.Dispose();
        }
    }

    private static void PublishFailure(long operationToken, uint status, string message)
    {
        byte[] error;
        try
        {
            error = Encoding.UTF8.GetBytes(message);
        }
        catch (EncoderFallbackException)
        {
            error = Encoding.UTF8.GetBytes("Managed order failed.");
        }

        if (error.Length > Codec.MaxBufferBytes)
            error = Encoding.UTF8.GetBytes("Managed order error exceeded the ABI limit.");

        PublishNativeResult(operationToken, status, [], 0, error, (uint)error.Length);
    }

    private sealed class BusinessOrdersAdapter(BusinessOrdersService orders) : Contracts.IOrdersService
    {
        public async Task<Contracts.OrderReceipt> PlaceAsync(
            Contracts.OrderDraft draft,
            CancellationToken cancellationToken)
        {
            var receipt = await orders.PlaceAsync(
                new BusinessOrderDraft(draft.Sku, draft.Quantity),
                cancellationToken).ConfigureAwait(false);
            return new Contracts.OrderReceipt(
                receipt.Id,
                receipt.Sku,
                receipt.Quantity);
        }
    }

    private sealed class NativeConfirmation : NetLift.Samples.Orders.IConfirmation
    {
        public async Task<bool> ConfirmAsync(string title, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (title.StartsWith("STREAM-OVERFLOW-", StringComparison.Ordinal))
                return true;

            var requestId = Interlocked.Increment(ref nextConfirmationId);
            if (requestId <= 0)
                throw new InvalidOperationException("Confirmation request identifiers are exhausted.");
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!PendingConfirmations.TryAdd(requestId, completion))
                throw new InvalidOperationException("Duplicate confirmation request ID.");

            using var cancellationRegistration = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));
            try
            {
                if (!completion.Task.IsCompleted &&
                    RequestNativeConfirmation(requestId, title) == 0)
                {
                    completion.TrySetException(
                        new InvalidOperationException("SwiftUI confirmation callback is unavailable."));
                }

                return await completion.Task.ConfigureAwait(false);
            }
            finally
            {
                PendingConfirmations.TryRemove(requestId, out _);
            }
        }
    }
}
