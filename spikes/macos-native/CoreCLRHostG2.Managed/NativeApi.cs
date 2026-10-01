using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NetLift;
using NetLift.Generated;

namespace NetLift.CoreCLRHostG2;

public static unsafe class NativeApi
{
    private const uint Success = 0;
    private const uint InvalidArgument = 1;
    private const uint NotReady = 2;
    private const uint SchemaMismatch = 5;
    private const uint BufferTooLarge = 6;
    private const uint ManagedFailure = 8;
    private static readonly byte[] SchemaHash = Convert.FromHexString(Contract.SchemaHash);
    private static G2DispatchRegistry? registry;
    private static int startState;
    private static long nextCorrelationToken;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int GetRuntimeMajor() => Environment.Version.Major;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint Describe(byte* output, uint outputCapacity)
    {
        if (output is null || outputCapacity < 32)
            return InvalidArgument;

        var descriptor = default(LiftContractDescriptorV1);
        var status = AndroidContractBootstrap.DescribeV1(
            &descriptor,
            (uint)sizeof(LiftContractDescriptorV1));
        if (status != (uint)LiftBootstrapStatus.Success)
            return status;

        byte* hash = descriptor.SchemaHash;
        new ReadOnlySpan<byte>(hash, 32).CopyTo(new Span<byte>(output, 32));
        return Success;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint Start(byte* expectedSchemaHash, uint expectedHashLength)
    {
        if (expectedSchemaHash is null || expectedHashLength != 32)
            return InvalidArgument;
        if (Interlocked.CompareExchange(ref startState, -1, 0) != 0)
            return InvalidArgument;

        try
        {
            var descriptor = default(LiftContractDescriptorV1);
            var describeStatus = AndroidContractBootstrap.DescribeV1(
                &descriptor,
                (uint)sizeof(LiftContractDescriptorV1));
            if (describeStatus != (uint)LiftBootstrapStatus.Success)
                return ResetStart((uint)LiftBootstrapStatus.ManagedFailure);

            byte* descriptorHash = descriptor.SchemaHash;
            if (!new ReadOnlySpan<byte>(descriptorHash, 32)
                    .SequenceEqual(new ReadOnlySpan<byte>(expectedSchemaHash, 32)) ||
                !new ReadOnlySpan<byte>(descriptorHash, 32).SequenceEqual(SchemaHash))
                return ResetStart(SchemaMismatch);

            var createdRegistry = new G2DispatchRegistry(
                descriptor.DescriptorVersion,
                descriptor.AbiVersion,
                descriptor.RequiredFeatureBits,
                SchemaHash);
            if (!LiftGeneratedHost.TrySetRegistry(createdRegistry))
                return ResetStart((uint)LiftBootstrapStatus.NotReady);

            var registerStatus = AndroidContractBootstrap.RegisterV1();
            if (registerStatus != (uint)LiftBootstrapStatus.Success)
            {
                LiftGeneratedHost.TryClearRegistry(createdRegistry);
                return ResetStart(registerStatus);
            }

            Volatile.Write(ref registry, createdRegistry);
            Volatile.Write(ref startState, 1);
            return Success;
        }
        catch (Exception)
        {
            return ResetStart((uint)LiftBootstrapStatus.ManagedFailure);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint FactoryCount() =>
        (uint)Volatile.Read(ref G2Probe.FactoryCreationCount);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint Invoke(
        uint serviceId,
        uint methodId,
        byte* arguments,
        uint argumentLength,
        byte* result,
        uint resultCapacity,
        uint* resultLength,
        delegate* unmanaged[Cdecl]<nint, uint, uint, byte*, uint, uint> nativeCallback,
        nint callbackContext)
    {
        if (resultLength is null ||
            (argumentLength != 0 && arguments is null) ||
            (resultCapacity != 0 && result is null))
            return InvalidArgument;
        *resultLength = 0;
        if (argumentLength > Codec.MaxBufferBytes || resultCapacity > Codec.MaxBufferBytes)
            return BufferTooLarge;

        var currentRegistry = Volatile.Read(ref registry);
        if (currentRegistry is null || Volatile.Read(ref startState) != 1)
            return NotReady;

        try
        {
            var request = new ReadOnlySpan<byte>(
                arguments, checked((int)argumentLength)).ToArray();
            var invocation = new LiftInvocationContext(
                CancellationToken.None,
                (ulong)Interlocked.Increment(ref nextCorrelationToken),
                new SwiftNativeServiceInvoker(nativeCallback, callbackContext));
            var dispatched = currentRegistry
                .InvokeAsync(serviceId, methodId, request, invocation)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            if (dispatched.Status != LiftDispatchStatus.Success)
                return (uint)dispatched.Status;
            if (dispatched.Payload.Length > resultCapacity)
                return BufferTooLarge;

            dispatched.Payload.Span.CopyTo(
                new Span<byte>(result, checked((int)resultCapacity)));
            *resultLength = checked((uint)dispatched.Payload.Length);
            return Success;
        }
        catch (Exception)
        {
            return ManagedFailure;
        }
    }

    private static uint ResetStart(uint status)
    {
        Volatile.Write(ref startState, 0);
        return status;
    }
}

internal sealed unsafe class SwiftNativeServiceInvoker(
    delegate* unmanaged[Cdecl]<nint, uint, uint, byte*, uint, uint> callback,
    nint callbackContext) : ILiftNativeServiceInvoker
{
    public ValueTask<LiftNativeReply> InvokeAsync(
        uint serviceId,
        uint methodId,
        ReadOnlyMemory<byte> payload,
        ulong correlationToken,
        CancellationToken cancellationToken)
    {
        _ = correlationToken;
        cancellationToken.ThrowIfCancellationRequested();
        if (serviceId != Contract.ServiceId_42 ||
            methodId != Contract.MethodId_42_3 ||
            callback is null ||
            payload.Length > Codec.MaxBufferBytes)
            return ValueTask.FromResult(new LiftNativeReply(
                LiftNativeReplyKind.Unavailable,
                ReadOnlyMemory<byte>.Empty,
                ReadOnlyMemory<byte>.Empty));

        var bytes = payload.ToArray();
        fixed (byte* data = bytes)
        {
            var accepted = callback(
                callbackContext,
                serviceId,
                methodId,
                data,
                (uint)bytes.Length);
            if (accepted > 1)
                return ValueTask.FromResult(new LiftNativeReply(
                    LiftNativeReplyKind.MalformedPayload,
                    ReadOnlyMemory<byte>.Empty,
                    ReadOnlyMemory<byte>.Empty));
            return ValueTask.FromResult(new LiftNativeReply(
                LiftNativeReplyKind.Result,
                new byte[] { (byte)accepted },
                ReadOnlyMemory<byte>.Empty));
        }
    }
}
