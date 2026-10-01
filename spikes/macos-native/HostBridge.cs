using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using NetLift.Samples.Orders;

namespace NetLift.Spikes.MacOS;

public static class HostBridge
{
    private static long nextOperationId;
    private static long nextRequestId;
    private static readonly ConcurrentDictionary<long, OperationState> Operations = new();
    private static readonly ConcurrentDictionary<(long OperationId, long RequestId), TaskCompletionSource<bool>> Confirmations = new();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetRuntimeMajor() => Environment.Version.Major;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe long PlaceOrder(
        byte* skuUtf8,
        int skuLength,
        int quantity,
        nint context,
        delegate* unmanaged[Cdecl]<long, long, byte*, int, nint, void> confirmation,
        delegate* unmanaged[Cdecl]<long, int, long, int, byte*, int, nint, void> completion)
    {
        if (skuUtf8 == null || skuLength <= 0 || quantity <= 0 ||
            confirmation == null || completion == null)
            return -1;

        try
        {
            var sku = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(skuUtf8, skuLength));
            var operationId = Interlocked.Increment(ref nextOperationId);
            var operation = new OperationState();
            if (!Operations.TryAdd(operationId, operation))
            {
                operation.Complete();
                return -1;
            }

            try
            {
                _ = Task.Run(() => RunOrderAsync(
                    operationId, sku, quantity, context, (nint)confirmation, (nint)completion, operation));
            }
            catch
            {
                Operations.TryRemove(operationId, out _);
                operation.Complete();
                throw;
            }
            return operationId;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not start managed order: {exception}");
            return -1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int Cancel(long operationId)
    {
        if (!Operations.TryGetValue(operationId, out var operation))
            return 0;

        try
        {
            return operation.Cancel() ? 1 : 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not cancel managed order {operationId}: {exception}");
            return -1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int ResolveConfirmation(long operationId, long requestId, int accepted)
    {
        return Confirmations.TryGetValue((operationId, requestId), out var confirmation) &&
               confirmation.TrySetResult(accepted != 0)
            ? 1
            : 0;
    }

    private static async Task RunOrderAsync(
        long operationId,
        string sku,
        int quantity,
        nint context,
        nint confirmationAddress,
        nint completionAddress,
        OperationState operation)
    {
        var cancellationToken = operation.Token;
        var status = 0;
        long receiptId = 0;
        var resultQuantity = 0;
        string? error = null;
        try
        {
            var service = new OrdersService(new NativeConfirmation(
                operationId, context, confirmationAddress));
            var receipt = await service.PlaceAsync(
                new OrderDraft(sku, quantity), cancellationToken).ConfigureAwait(false);
            receiptId = receipt.Id;
            resultQuantity = receipt.Quantity;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            status = 1;
        }
        catch (Exception exception)
        {
            status = 2;
            error = exception.Message;
            Console.Error.WriteLine($"Managed order failed: {exception}");
        }
        finally
        {
            Operations.TryRemove(operationId, out _);
            operation.Complete();
        }

        var errorBytes = error is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(error);
        unsafe
        {
            fixed (byte* errorPointer = errorBytes)
            {
                ((delegate* unmanaged[Cdecl]<long, int, long, int, byte*, int, nint, void>)completionAddress)(
                    operationId, status, receiptId, resultQuantity,
                    errorBytes.Length == 0 ? null : errorPointer,
                    errorBytes.Length, context);
            }
        }
    }

    private sealed class NativeConfirmation(
        long operationId,
        nint context,
        nint callbackAddress) : IConfirmation
    {
        public async Task<bool> ConfirmAsync(string title, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestId = Interlocked.Increment(ref nextRequestId);
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!Confirmations.TryAdd((operationId, requestId), completion))
                throw new InvalidOperationException("Could not register the confirmation request.");

            try
            {
                var skuBytes = Encoding.UTF8.GetBytes(title);
                unsafe
                {
                    fixed (byte* skuPointer = skuBytes)
                    {
                        ((delegate* unmanaged[Cdecl]<long, long, byte*, int, nint, void>)callbackAddress)(
                            operationId, requestId, skuPointer, skuBytes.Length, context);
                    }
                }

                return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Confirmations.TryRemove((operationId, requestId), out _);
            }
        }
    }

    private sealed class OperationState
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource cancellation = new();
        private int activeCancellations;
        private bool completed;
        private bool disposed;

        public CancellationToken Token => cancellation.Token;

        public bool Cancel()
        {
            lock (gate)
            {
                if (completed)
                    return false;
                activeCancellations++;
            }

            try
            {
                cancellation.Cancel();
                return true;
            }
            finally
            {
                lock (gate)
                {
                    activeCancellations--;
                    DisposeIfComplete();
                }
            }
        }

        public void Complete()
        {
            lock (gate)
            {
                completed = true;
                DisposeIfComplete();
            }
        }

        private void DisposeIfComplete()
        {
            if (completed && activeCancellations == 0 && !disposed)
            {
                disposed = true;
                cancellation.Dispose();
            }
        }
    }
}
