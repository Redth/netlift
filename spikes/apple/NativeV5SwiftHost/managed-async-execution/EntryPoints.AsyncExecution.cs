extern alias AsyncOld;
extern alias AsyncNew;

using System.Runtime.CompilerServices;
using OldAsyncRuntime = AsyncOld::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi;
using NewAsyncRuntime = AsyncNew::NetLift.Negotiation.NativeHost.AsyncRuntime.AsyncRuntimeApi;

namespace NetLift.V5.AppHost;

public static unsafe partial class EntryPoints
{
    public static int OldConfigureAsyncCallbacks(nint requestCallback, nint publishResultCallback) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, int>)&OldAsyncRuntime.ConfigureNativeCallbacks)(
            requestCallback,
            publishResultCallback);

    public static int NewConfigureAsyncCallbacks(nint requestCallback, nint publishResultCallback) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, int>)&NewAsyncRuntime.ConfigureNativeCallbacks)(
            requestCallback,
            publishResultCallback);

    public static int OldRegisterManagedResultContext(
        ulong endpointToken,
        byte* descriptor,
        uint descriptorLength,
        byte* expectedPeerHash,
        uint expectedPeerHashLength) =>
        ((delegate* unmanaged[Cdecl]<ulong, byte*, uint, byte*, uint, int>)
            &OldAsyncRuntime.RegisterManagedResultContext)(
                endpointToken,
                descriptor,
                descriptorLength,
                expectedPeerHash,
                expectedPeerHashLength);

    public static int NewRegisterManagedResultContext(
        ulong endpointToken,
        byte* descriptor,
        uint descriptorLength,
        byte* expectedPeerHash,
        uint expectedPeerHashLength) =>
        ((delegate* unmanaged[Cdecl]<ulong, byte*, uint, byte*, uint, int>)
            &NewAsyncRuntime.RegisterManagedResultContext)(
                endpointToken,
                descriptor,
                descriptorLength,
                expectedPeerHash,
                expectedPeerHashLength);

    public static int OldRegisterGeneratedHost(
        ulong endpointToken,
        byte* peerRequestSchema,
        uint peerRequestSchemaLength,
        byte* managedResultSchema,
        uint managedResultSchemaLength) =>
        ((delegate* unmanaged[Cdecl]<ulong, byte*, uint, byte*, uint, int>)
            &OldAsyncRuntime.RegisterGeneratedHost)(
                endpointToken,
                peerRequestSchema,
                peerRequestSchemaLength,
                managedResultSchema,
                managedResultSchemaLength);

    public static int NewRegisterGeneratedHost(
        ulong endpointToken,
        byte* peerRequestSchema,
        uint peerRequestSchemaLength,
        byte* managedResultSchema,
        uint managedResultSchemaLength) =>
        ((delegate* unmanaged[Cdecl]<ulong, byte*, uint, byte*, uint, int>)
            &NewAsyncRuntime.RegisterGeneratedHost)(
                endpointToken,
                peerRequestSchema,
                peerRequestSchemaLength,
                managedResultSchema,
                managedResultSchemaLength);

    public static int OldUnregisterGeneratedHost(ulong endpointToken) =>
        ((delegate* unmanaged[Cdecl]<ulong, int>)&OldAsyncRuntime.UnregisterGeneratedHost)(endpointToken);

    public static int NewUnregisterGeneratedHost(ulong endpointToken) =>
        ((delegate* unmanaged[Cdecl]<ulong, int>)&NewAsyncRuntime.UnregisterGeneratedHost)(endpointToken);

    public static int OldStartRequestV2(
        ulong endpointToken,
        ulong registrationToken,
        ulong registrationGeneration,
        ulong operationToken,
        byte* request,
        uint requestLength) =>
        ((delegate* unmanaged[Cdecl]<ulong, ulong, ulong, ulong, byte*, uint, int>)
            &OldAsyncRuntime.StartRequestV2)(
                endpointToken,
                registrationToken,
                registrationGeneration,
                operationToken,
                request,
                requestLength);

    public static int NewStartRequestV2(
        ulong endpointToken,
        ulong registrationToken,
        ulong registrationGeneration,
        ulong operationToken,
        byte* request,
        uint requestLength) =>
        ((delegate* unmanaged[Cdecl]<ulong, ulong, ulong, ulong, byte*, uint, int>)
            &NewAsyncRuntime.StartRequestV2)(
                endpointToken,
                registrationToken,
                registrationGeneration,
                operationToken,
                request,
                requestLength);

    public static int OldCancelOperationV2(
        ulong registrationToken,
        ulong registrationGeneration,
        ulong operationToken) =>
        ((delegate* unmanaged[Cdecl]<ulong, ulong, ulong, int>)&OldAsyncRuntime.CancelOperationV2)(
            registrationToken,
            registrationGeneration,
            operationToken);

    public static int NewCancelOperationV2(
        ulong registrationToken,
        ulong registrationGeneration,
        ulong operationToken) =>
        ((delegate* unmanaged[Cdecl]<ulong, ulong, ulong, int>)&NewAsyncRuntime.CancelOperationV2)(
            registrationToken,
            registrationGeneration,
            operationToken);

    public static int OldReplyConfirmationV2(
        ulong registrationToken,
        ulong registrationGeneration,
        ulong requestToken,
        uint outcome,
        uint accepted,
        byte* diagnostic,
        uint diagnosticLength) =>
        ((delegate* unmanaged[Cdecl]<ulong, ulong, ulong, uint, uint, byte*, uint, int>)
            &OldAsyncRuntime.ReplyConfirmationV2)(
                registrationToken,
                registrationGeneration,
                requestToken,
                outcome,
                accepted,
                diagnostic,
                diagnosticLength);

    public static int NewReplyConfirmationV2(
        ulong registrationToken,
        ulong registrationGeneration,
        ulong requestToken,
        uint outcome,
        uint accepted,
        byte* diagnostic,
        uint diagnosticLength) =>
        ((delegate* unmanaged[Cdecl]<ulong, ulong, ulong, uint, uint, byte*, uint, int>)
            &NewAsyncRuntime.ReplyConfirmationV2)(
                registrationToken,
                registrationGeneration,
                requestToken,
                outcome,
                accepted,
                diagnostic,
                diagnosticLength);

    public static int OldCancelConfirmationV2(
        ulong registrationToken,
        ulong registrationGeneration,
        ulong requestToken) =>
        ((delegate* unmanaged[Cdecl]<ulong, ulong, ulong, int>)&OldAsyncRuntime.CancelConfirmationV2)(
            registrationToken,
            registrationGeneration,
            requestToken);

    public static int NewCancelConfirmationV2(
        ulong registrationToken,
        ulong registrationGeneration,
        ulong requestToken) =>
        ((delegate* unmanaged[Cdecl]<ulong, ulong, ulong, int>)&NewAsyncRuntime.CancelConfirmationV2)(
            registrationToken,
            registrationGeneration,
            requestToken);

    public static int OldDecodeResult(
        ulong endpointToken,
        byte* descriptor,
        uint descriptorLength,
        byte* result,
        uint resultLength,
        int* code,
        uint* accepted,
        uint* audited) =>
        ((delegate* unmanaged[Cdecl]<ulong, byte*, uint, byte*, uint, int*, uint*, uint*, int>)
            &OldAsyncRuntime.DecodeResult)(
                endpointToken,
                descriptor,
                descriptorLength,
                result,
                resultLength,
                code,
                accepted,
                audited);

    public static int NewDecodeResult(
        ulong endpointToken,
        byte* descriptor,
        uint descriptorLength,
        byte* result,
        uint resultLength,
        int* code,
        uint* accepted,
        uint* audited) =>
        ((delegate* unmanaged[Cdecl]<ulong, byte*, uint, byte*, uint, int*, uint*, uint*, int>)
            &NewAsyncRuntime.DecodeResult)(
                endpointToken,
                descriptor,
                descriptorLength,
                result,
                resultLength,
                code,
                accepted,
                audited);

    public static int OldInvalidateManagedResultContext(ulong endpointToken) =>
        ((delegate* unmanaged[Cdecl]<ulong, int>)&OldAsyncRuntime.InvalidateManagedResultContext)(endpointToken);

    public static int NewInvalidateManagedResultContext(ulong endpointToken) =>
        ((delegate* unmanaged[Cdecl]<ulong, int>)&NewAsyncRuntime.InvalidateManagedResultContext)(endpointToken);

    public static int OldGetOutstandingManagedWork() =>
        ((delegate* unmanaged[Cdecl]<int>)&OldAsyncRuntime.GetOutstandingManagedWork)();

    public static int NewGetOutstandingManagedWork() =>
        ((delegate* unmanaged[Cdecl]<int>)&NewAsyncRuntime.GetOutstandingManagedWork)();

    public static int OldGetOutstandingManagedResultLeases() =>
        ((delegate* unmanaged[Cdecl]<int>)&OldAsyncRuntime.GetOutstandingManagedResultLeases)();

    public static int NewGetOutstandingManagedResultLeases() =>
        ((delegate* unmanaged[Cdecl]<int>)&NewAsyncRuntime.GetOutstandingManagedResultLeases)();
}
