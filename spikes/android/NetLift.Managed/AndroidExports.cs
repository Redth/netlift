using System.Runtime.InteropServices;
using System.Text;
using NetLift.Samples.Orders;

namespace NetLift.Managed;

public static class AndroidExports
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static int GetRuntimeVersionMajor() => Environment.Version.Major;

    public static long PlaceSampleOrder(
        IntPtr skuUtf8,
        int skuByteCount,
        int quantity,
        ulong operationToken)
    {
        if (skuByteCount < 0 || skuByteCount > 4096 ||
            (skuUtf8 == IntPtr.Zero && skuByteCount != 0) ||
            operationToken == 0)
            return -3;

        var skuBytes = new byte[skuByteCount];
        if (skuByteCount != 0)
            Marshal.Copy(skuUtf8, skuBytes, 0, skuByteCount);

        string sku;
        try
        {
            sku = StrictUtf8.GetString(skuBytes);
        }
        catch (DecoderFallbackException)
        {
            return -3;
        }

        var service = new OrdersService(new KotlinConfirmation(operationToken));

        OrderReceipt receipt;
        try
        {
            receipt = service.PlaceAsync(
                    new OrderDraft(sku, quantity))
                .GetAwaiter()
                .GetResult();
        }
        catch (InvalidOperationException exception) when (exception.Message == "Order was declined.")
        {
            return -2;
        }
        catch (Exception)
        {
            return -4;
        }

        return receipt.Id;
    }

    private sealed class KotlinConfirmation(ulong operationToken) : IConfirmation
    {
        public Task<bool> ConfirmAsync(string title, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] titleUtf8;
            try
            {
                titleUtf8 = StrictUtf8.GetBytes(title);
            }
            catch (EncoderFallbackException exception)
            {
                return Task.FromException<bool>(exception);
            }
            return Task.Run(
                () => RequestKotlinConfirmation(operationToken, titleUtf8, titleUtf8.Length) == 1,
                cancellationToken);
        }
    }

    [DllImport("netlift_host", EntryPoint = "NetLift_ConfirmFromKotlin", CallingConvention = CallingConvention.Cdecl)]
    private static extern int RequestKotlinConfirmation(
        ulong operationToken,
        [In] byte[] titleUtf8,
        int titleByteCount);
}
