using Old = NetLift.Negotiation.NativeHost.Managed.Old;
using New = NetLift.Negotiation.NativeHost.Managed.New;

namespace NetLift.V5.AppHost;

public static unsafe partial class EntryPoints
{
    public static uint OldGetManagedResultSchema(
        ulong token,
        uint serviceId,
        uint methodId,
        byte* destination,
        uint capacity,
        uint* bytesWritten,
        uint* requiredBytes) =>
        ((delegate* unmanaged[Cdecl]<ulong, uint, uint, byte*, uint, uint*, uint*, uint>)
            &Old.NativeApi.GetManagedResultSchema)(
                token, serviceId, methodId, destination, capacity, bytesWritten, requiredBytes);

    public static uint NewGetManagedResultSchema(
        ulong token,
        uint serviceId,
        uint methodId,
        byte* destination,
        uint capacity,
        uint* bytesWritten,
        uint* requiredBytes) =>
        ((delegate* unmanaged[Cdecl]<ulong, uint, uint, byte*, uint, uint*, uint*, uint>)
            &New.NativeApi.GetManagedResultSchema)(
                token, serviceId, methodId, destination, capacity, bytesWritten, requiredBytes);
}
