using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NetLift.Samples.Orders;

namespace NetLift.Spikes.Desktop;

public static class HostBridge
{
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static unsafe int Start(
        int quantity,
        delegate* unmanaged[Cdecl]<int, int> confirm,
        delegate* unmanaged[Cdecl]<int, int> complete)
    {
        if (quantity <= 0 || confirm == null || complete == null)
            return -1;

        var confirmAddress = (nint)confirm;
        var completeAddress = (nint)complete;

        _ = Task.Run(() => RunAsync(quantity, confirmAddress, completeAddress));
        return 0;
    }

    private static async Task RunAsync(int quantity, nint confirmAddress, nint completeAddress)
    {
        int result;
        try
        {
            var orders = new OrdersService(new NativeConfirmation(confirmAddress));
            var receipt = await orders.PlaceAsync(new OrderDraft("ABC", quantity));
            result = receipt.Quantity;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Managed desktop spike failed: {exception}");
            result = -2;
        }
        Complete(completeAddress, result);
    }

    private static unsafe void Complete(nint callback, int result) =>
        ((delegate* unmanaged[Cdecl]<int, int>)callback)(result);

    private sealed unsafe class NativeConfirmation(nint callback) : IConfirmation
    {
        public Task<bool> ConfirmAsync(string title, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = ((delegate* unmanaged[Cdecl]<int, int>)callback)(title.Length);
            return Task.FromResult(result != 0);
        }
    }
}
