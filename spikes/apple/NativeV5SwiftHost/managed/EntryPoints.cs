using System.Runtime.CompilerServices;
using Old = NetLift.Negotiation.NativeHost.Managed.Old;
using New = NetLift.Negotiation.NativeHost.Managed.New;
using LiftV5BytesView = NetLift.Negotiation.NativeHost.Managed.Old.LiftV5BytesView;
using LiftV5Counters = NetLift.Negotiation.NativeHost.Managed.Old.LiftV5Counters;
using LiftV5OwnedBytes = NetLift.Negotiation.NativeHost.Managed.Old.LiftV5OwnedBytes;
using LiftV5OwnedPeerSchema = NetLift.Negotiation.NativeHost.Managed.Old.LiftV5OwnedPeerSchema;
using LiftV5Status = NetLift.Negotiation.NativeHost.Managed.Old.LiftV5Status;

namespace NetLift.V5.AppHost;

public static unsafe partial class EntryPoints
{
    public static uint OldGetManifest(LiftV5OwnedBytes* output, LiftV5Status* status) =>
        ((delegate* unmanaged[Cdecl]<LiftV5OwnedBytes*, LiftV5Status*, uint>)
            &Old.NativeApi.GetManifest)(output, status);

    public static uint OldGetPeerSchema(
        ulong token, LiftV5OwnedPeerSchema* output, LiftV5Status* status) =>
        ((delegate* unmanaged[Cdecl]<ulong, LiftV5OwnedPeerSchema*, LiftV5Status*, uint>)
            &Old.NativeApi.GetPeerSchema)(token, output, status);

    public static uint OldCreateEndpoint(
        uint optIn, uint minimumMajor, uint minimumMinor, ulong* token, LiftV5Status* status) =>
        ((delegate* unmanaged[Cdecl]<uint, uint, uint, ulong*, LiftV5Status*, uint>)
            &Old.NativeApi.CreateEndpoint)(optIn, minimumMajor, minimumMinor, token, status);

    public static uint OldStart(
        ulong token,
        LiftV5BytesView* manifest,
        LiftV5BytesView* hello,
        LiftV5BytesView* trustedHash,
        uint minimumMajor,
        uint minimumMinor,
        uint peerOptIn,
        LiftV5Status* status) =>
        ((delegate* unmanaged[Cdecl]<ulong, LiftV5BytesView*, LiftV5BytesView*, LiftV5BytesView*,
            uint, uint, uint, LiftV5Status*, uint>)
            &Old.NativeApi.Start)(
                token, manifest, hello, trustedHash, minimumMajor, minimumMinor, peerOptIn, status);

#if !NETLIFT_V5_ASYNC_RESULT
    public static uint OldDispatch(
        ulong token, LiftV5BytesView* payload, int* result, LiftV5Status* status) =>
        ((delegate* unmanaged[Cdecl]<ulong, LiftV5BytesView*, int*, LiftV5Status*, uint>)
            &Old.NativeApi.Dispatch)(token, payload, result, status);

    public static uint OldGetCounters(LiftV5Counters* counters, LiftV5Status* status) =>
        ((delegate* unmanaged[Cdecl]<LiftV5Counters*, LiftV5Status*, uint>)
            &Old.NativeApi.GetCounters)(counters, status);
#endif

    public static uint OldCloseEndpoint(ulong token, LiftV5Status* status) =>
        ((delegate* unmanaged[Cdecl]<ulong, LiftV5Status*, uint>)
            &Old.NativeApi.CloseEndpoint)(token, status);

    public static uint OldFreeBuffer(void* buffer, LiftV5Status* status) =>
        ((delegate* unmanaged[Cdecl]<void*, LiftV5Status*, uint>)
            &Old.NativeApi.FreeBuffer)(buffer, status);

    public static uint NewGetManifest(
        New.LiftV5OwnedBytes* output, New.LiftV5Status* status) =>
        ((delegate* unmanaged[Cdecl]<New.LiftV5OwnedBytes*, New.LiftV5Status*, uint>)
            &New.NativeApi.GetManifest)(output, status);

    public static uint NewGetPeerSchema(
        ulong token, New.LiftV5OwnedPeerSchema* output, New.LiftV5Status* status) =>
        ((delegate* unmanaged[Cdecl]<ulong, New.LiftV5OwnedPeerSchema*, New.LiftV5Status*, uint>)
            &New.NativeApi.GetPeerSchema)(token, output, status);

    public static uint NewCreateEndpoint(
        uint optIn, uint minimumMajor, uint minimumMinor, ulong* token, New.LiftV5Status* status) =>
        ((delegate* unmanaged[Cdecl]<uint, uint, uint, ulong*, New.LiftV5Status*, uint>)
            &New.NativeApi.CreateEndpoint)(optIn, minimumMajor, minimumMinor, token, status);

    public static uint NewStart(
        ulong token,
        New.LiftV5BytesView* manifest,
        New.LiftV5BytesView* hello,
        New.LiftV5BytesView* trustedHash,
        uint minimumMajor,
        uint minimumMinor,
        uint peerOptIn,
        New.LiftV5Status* status) =>
        ((delegate* unmanaged[Cdecl]<ulong, New.LiftV5BytesView*, New.LiftV5BytesView*,
            New.LiftV5BytesView*, uint, uint, uint, New.LiftV5Status*, uint>)
            &New.NativeApi.Start)(
                token, manifest, hello, trustedHash, minimumMajor, minimumMinor, peerOptIn, status);

#if !NETLIFT_V5_ASYNC_RESULT
    public static uint NewDispatch(
        ulong token, New.LiftV5BytesView* payload, int* result, New.LiftV5Status* status) =>
        ((delegate* unmanaged[Cdecl]<ulong, New.LiftV5BytesView*, int*, New.LiftV5Status*, uint>)
            &New.NativeApi.Dispatch)(token, payload, result, status);

    public static uint NewGetCounters(
        New.LiftV5Counters* counters, New.LiftV5Status* status) =>
        ((delegate* unmanaged[Cdecl]<New.LiftV5Counters*, New.LiftV5Status*, uint>)
            &New.NativeApi.GetCounters)(counters, status);
#endif

    public static uint NewCloseEndpoint(ulong token, New.LiftV5Status* status) =>
        ((delegate* unmanaged[Cdecl]<ulong, New.LiftV5Status*, uint>)
            &New.NativeApi.CloseEndpoint)(token, status);

    public static uint NewFreeBuffer(void* buffer, New.LiftV5Status* status) =>
        ((delegate* unmanaged[Cdecl]<void*, New.LiftV5Status*, uint>)
            &New.NativeApi.FreeBuffer)(buffer, status);
}
