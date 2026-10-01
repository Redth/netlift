using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NetLift.Generated;

namespace NetLift.NativeHost.Managed;

public static unsafe partial class NativeApi
{
    private const uint AbiVersion = 1;
    private const uint MaxBufferBytes = (uint)Codec.MaxBufferBytes;
    private const uint SyncFeature = 1;
    private const uint AsyncFeature = 2;
    private const uint TypedNativeReplyFeature = 4;
    private const uint Ok = 0;
    private const uint InvalidArgument = 1;
    private const uint NotReady = 2;
    private const uint Unsupported = 3;
    private const uint VersionMismatch = 4;
    private const uint SchemaMismatch = 5;
    private const uint BufferTooLarge = 6;
    private const uint MalformedBuffer = 7;
    private const uint ManagedFailure = 8;
    private const uint InvalidRelease = 9;
    private const uint StaleHandle = 10;
    private const uint Unavailable = 11;
    private const uint Cancelled = 12;

    private static readonly byte[] ExpectedSchemaHash = Convert.FromHexString(Contract.SchemaHash);
    internal static readonly bool TypedNativeReplyEnabled = SchemaDeclaresTypedNativeReply();
    private static readonly ConcurrentDictionary<nint, byte> AllocatedBuffers = new();
    private static readonly ITransformService Service = new TransformService();
    private static int _started;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint Start(LiftConfig* config, LiftRuntimeInfo* info, LiftStatus* status)
    {
        if (status is null)
            return InvalidArgument;
        if (status->Message.Data is not null || status->Message.Length != 0)
            return InvalidArgument;
        *status = default;
        if (config is null || info is null)
            return Fail(status, InvalidArgument, "Required ABI pointer is null.");

        var configSize = config->StructSize;
        var outputSize = info->StructSize;

        try
        {
            if (configSize < (uint)sizeof(LiftConfig) || outputSize < (uint)sizeof(LiftRuntimeInfo))
                return Fail(status, InvalidArgument, "ABI structure is smaller than version 1.");
            if (config->AbiVersion != AbiVersion)
                return Fail(status, VersionMismatch, "ABI version mismatch.");

            byte* configHash = config->SchemaHash;
            if (!new ReadOnlySpan<byte>(configHash, ExpectedSchemaHash.Length).SequenceEqual(ExpectedSchemaHash))
                return Fail(status, SchemaMismatch, "Contract schema hash mismatch.");

            Volatile.Write(ref _started, 1);
            info->AbiVersion = AbiVersion;
            info->FeatureBits = SyncFeature | AsyncFeature |
                (TypedNativeReplyEnabled ? TypedNativeReplyFeature : 0);
            byte* outputHash = info->SchemaHash;
            ExpectedSchemaHash.AsSpan().CopyTo(new Span<byte>(outputHash, ExpectedSchemaHash.Length));
            return Ok;
        }
        catch (Exception)
        {
            return Fail(status, ManagedFailure, "Managed startup failed.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint Invoke(
        uint serviceId,
        uint methodId,
        LiftBytesView* arguments,
        LiftOwnedBytes* result,
        LiftStatus* status)
    {
        if (result is null || status is null)
            return InvalidArgument;
        if (result->Data is not null || result->Length != 0 ||
            status->Message.Data is not null || status->Message.Length != 0)
            return InvalidArgument;
        *result = default;
        *status = default;
        if (arguments is null)
            return Fail(status, InvalidArgument, "Input descriptor pointer is null.");
        if (Volatile.Read(ref _started) == 0)
            return Fail(status, NotReady, "Bridge is not started.");
        if (serviceId != 1 || methodId != 1)
            return Fail(status, Unsupported, "Unknown service or method ID.");
        if (arguments->Length > MaxBufferBytes)
            return Fail(status, BufferTooLarge, "Input exceeds the maximum buffer size.");
        if (arguments->Length != 0 && arguments->Data is null)
            return Fail(status, InvalidArgument, "Input data pointer is null.");

        try
        {
            var input = new ReadOnlySpan<byte>(arguments->Data, checked((int)arguments->Length));
            if (!TryDecodePoint(input, out var point))
                return Fail(status, MalformedBuffer, "Input is not a valid Point v1 payload.");

            var transformed = Dispatch.Dispatch_1_1(Service, point);
            Span<byte> encoded = stackalloc byte[20];
            EncodeField(encoded, 0, 1, transformed.X);
            EncodeField(encoded, 10, 2, transformed.Y);
            result->Data = Allocate(encoded);
            result->Length = (uint)encoded.Length;
            return Ok;
        }
        catch (OverflowException)
        {
            return Fail(status, ManagedFailure, "Managed method failed.");
        }
        catch (Exception)
        {
            return Fail(status, ManagedFailure, "Managed invocation failed.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint Free(void* buffer)
    {
        if (buffer is null || !AllocatedBuffers.TryRemove((nint)buffer, out _))
            return InvalidRelease;
        NativeMemory.Free(buffer);
        return Ok;
    }

    private static bool TryDecodePoint(ReadOnlySpan<byte> input, out Point point)
    {
        point = default!;
        if (input.Length != 20 ||
            !TryReadField(input, 0, expectedId: 1, out var x) ||
            !TryReadField(input, 10, expectedId: 2, out var y))
            return false;

        point = new Point(x, y);
        return true;
    }

    private static bool TryReadField(ReadOnlySpan<byte> input, int offset, ushort expectedId, out int value)
    {
        value = default;
        if (BinaryPrimitives.ReadUInt16LittleEndian(input.Slice(offset, 2)) != expectedId ||
            BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(offset + 2, 4)) != sizeof(int))
            return false;

        value = BinaryPrimitives.ReadInt32LittleEndian(input.Slice(offset + 6, 4));
        return true;
    }

    private static void EncodeField(Span<byte> output, int offset, ushort id, int value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(output.Slice(offset, 2), id);
        BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(offset + 2, 4), sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(output.Slice(offset + 6, 4), value);
    }

    private static uint Fail(LiftStatus* status, uint code, string message)
    {
        if (status is null)
            return code;

        status->Code = code;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            status->Message.Data = Allocate(bytes);
            status->Message.Length = (uint)bytes.Length;
        }
        catch (Exception)
        {
            status->Message = default;
        }
        return code;
    }

    private static bool SchemaDeclaresTypedNativeReply()
    {
        using var schema = JsonDocument.Parse(Contract.CanonicalJson);
        return schema.RootElement.TryGetProperty("services", out var services) &&
            services.EnumerateArray().Any(service =>
                service.TryGetProperty("replyProtocol", out var protocol) &&
                protocol.ValueKind == JsonValueKind.String &&
                protocol.GetString() == "typed-v1");
    }

    private static byte* Allocate(ReadOnlySpan<byte> bytes)
    {
        var memory = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
        if (memory is null)
            throw new OutOfMemoryException();
        bytes.CopyTo(new Span<byte>(memory, bytes.Length));
        if (!AllocatedBuffers.TryAdd((nint)memory, 0))
        {
            NativeMemory.Free(memory);
            throw new InvalidOperationException("Native output address was already tracked.");
        }
        return memory;
    }
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LiftConfig
{
    public uint StructSize;
    public uint AbiVersion;
    public fixed byte SchemaHash[32];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LiftRuntimeInfo
{
    public uint StructSize;
    public uint AbiVersion;
    public uint FeatureBits;
    public fixed byte SchemaHash[32];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LiftBytesView
{
    public byte* Data;
    public uint Length;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct LiftOwnedBytes
{
    public byte* Data;
    public uint Length;
}

[StructLayout(LayoutKind.Sequential)]
public struct LiftStatus
{
    public uint Code;
    public LiftOwnedBytes Message;
}
