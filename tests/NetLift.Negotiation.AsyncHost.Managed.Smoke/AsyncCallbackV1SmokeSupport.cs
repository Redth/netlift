using System.Buffers.Binary;
using System.Text;
using NetLift;

internal sealed record AsyncCallbackV1SmokeInvocation(
    LiftInvocationContext Context,
    AsyncCallbackV1SmokeSupport.AsyncCallbackV1SmokeInvoker NativeInvoker,
    LiftAsyncCallbackV1HostDescriptor Descriptor);

internal static class AsyncCallbackV1SmokeSupport
{
    private const uint FeatureBits = 0x36;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static AsyncCallbackV1SmokeInvocation CreateInvocation(
        bool isNew,
        CancellationToken cancellationToken,
        bool confirmationAccepted,
        ulong endpointToken,
        bool? peerIsNew = null)
    {
        var schemaHash = Convert.FromHexString(isNew
            ? "969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4"
            : "d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b");
        var peerContractIsNew = peerIsNew ?? isNew;
        var peerSchemaHash = Convert.FromHexString(peerContractIsNew
            ? "969798a6f7c8cb9a07a9b459d6a0c390c0dcb7017de373360f1f96b1f0d66ab4"
            : "d0a299648c1f4479aa800f60634af097f1c3535c42b5710fae0eaa0fbf9b883b");
        var peerRequestSchema = CreateNlps(peerSchemaHash, peerContractIsNew);
        var managedResultSchema = CreateNlmr(
            schemaHash, peerSchemaHash, isNew, endpointToken);
        var descriptor = new LiftAsyncCallbackV1HostDescriptor(
            ProfileId: 1,
            AbiVersion: 1,
            SchemaVersion: 5,
            FeatureBits: FeatureBits,
            RequiredFeatureBits: FeatureBits,
            ManagedServiceId: 17,
            ManagedMethodId: 1,
            RequestDtoId: 21,
            RequestDtoVersion: 1,
            ResultDtoId: 22,
            ResultDtoVersion: 1,
            NativeServiceId: 30,
            NativeMethodId: 1,
            EndpointToken: endpointToken,
            LocalSchemaHash: schemaHash,
            PeerSchemaHash: peerSchemaHash,
            PeerRequestSchema: peerRequestSchema,
            ManagedResultSchema: managedResultSchema);
        var nativeInvoker = new AsyncCallbackV1SmokeInvoker(confirmationAccepted);
        var correlationToken = endpointToken + 1;
        var context = new LiftInvocationContext(
            cancellationToken, correlationToken, nativeInvoker)
        {
            EndpointToken = endpointToken,
            RegistrationToken = endpointToken + 2,
            RegistrationGeneration = 1,
            OperationToken = endpointToken + 3,
            PeerRequestSchema = peerRequestSchema,
            ManagedResultSchema = managedResultSchema
        };
        return new AsyncCallbackV1SmokeInvocation(context, nativeInvoker, descriptor);
    }

    public static ReadOnlyMemory<byte> EncodeOldRequest(string name, int count)
    {
        var nameBytes = StrictUtf8.GetBytes(name);
        var bodyLength = 2 * 7 + nameBytes.Length + sizeof(int);
        var bytes = new byte[12 + bodyLength];
        "NLF1"u8.CopyTo(bytes);
        WriteUInt16(bytes, 4, 1);
        WriteUInt16(bytes, 6, 2);
        WriteUInt32(bytes, 8, (uint)bodyLength);

        WriteUInt16(bytes, 12, 1);
        bytes[14] = 1;
        WriteUInt32(bytes, 15, (uint)nameBytes.Length);
        nameBytes.CopyTo(bytes.AsSpan(19));

        var countOffset = 19 + nameBytes.Length;
        WriteUInt16(bytes, countOffset, 2);
        bytes[countOffset + 2] = 2;
        WriteUInt32(bytes, countOffset + 3, sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(countOffset + 7), count);
        return bytes;
    }

    public static ReadOnlyMemory<byte> EncodeNewRequest(string name, int count, string note)
    {
        var nameBytes = StrictUtf8.GetBytes(name);
        var noteBytes = StrictUtf8.GetBytes(note);
        var fieldCount = noteBytes.Length == 0 ? 2 : 3;
        var bodyLength = fieldCount * 7 + nameBytes.Length + sizeof(int) + noteBytes.Length;
        var bytes = new byte[12 + bodyLength];
        "NLF1"u8.CopyTo(bytes);
        WriteUInt16(bytes, 4, 1);
        WriteUInt16(bytes, 6, (ushort)fieldCount);
        WriteUInt32(bytes, 8, (uint)bodyLength);

        WriteUInt16(bytes, 12, 1);
        bytes[14] = 1;
        WriteUInt32(bytes, 15, (uint)nameBytes.Length);
        nameBytes.CopyTo(bytes.AsSpan(19));

        var countOffset = 19 + nameBytes.Length;
        WriteUInt16(bytes, countOffset, 2);
        bytes[countOffset + 2] = 2;
        WriteUInt32(bytes, countOffset + 3, sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(countOffset + 7), count);
        if (noteBytes.Length != 0)
        {
            var noteOffset = countOffset + 7 + sizeof(int);
            WriteUInt16(bytes, noteOffset, 7);
            bytes[noteOffset + 2] = 1;
            WriteUInt32(bytes, noteOffset + 3, (uint)noteBytes.Length);
            noteBytes.CopyTo(bytes.AsSpan(noteOffset + 7));
        }
        return bytes;
    }

    public static (int Code, bool Accepted) DecodeOldResult(ReadOnlyMemory<byte> payload)
    {
        var bytes = payload.Span;
        if (bytes.Length < 12 + 2 * 7 + sizeof(int) + sizeof(byte) ||
            !bytes[..4].SequenceEqual("NLF1"u8) ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]) != 2 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != bytes.Length - 12)
            throw new InvalidOperationException("Generated old-fixture result was not canonical NLF1.");

        var offset = 12;
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]) != 1 ||
            bytes[offset + 2] != 2 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 3)..]) != sizeof(int))
            throw new InvalidOperationException("Generated old-fixture result code field was invalid.");
        var code = BinaryPrimitives.ReadInt32LittleEndian(bytes[(offset + 7)..]);
        offset += 7 + sizeof(int);
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]) != 2 ||
            bytes[offset + 2] != 3 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 3)..]) != sizeof(byte) ||
            bytes[offset + 7] > 1 ||
            offset + 8 != bytes.Length)
            throw new InvalidOperationException("Generated old-fixture result accepted field was invalid.");

        return (code, bytes[offset + 7] == 1);
    }

    public static (int Code, bool Accepted, bool Audited) DecodeNewResult(
        ReadOnlyMemory<byte> payload)
    {
        var bytes = payload.Span;
        if (bytes.Length < 12 ||
            !bytes[..4].SequenceEqual("NLF1"u8) ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != bytes.Length - 12)
            throw new InvalidOperationException("Generated new-fixture result was not canonical NLF1.");

        var fieldCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        var offset = 12;
        var code = 0;
        var accepted = false;
        var audited = false;
        for (var fieldIndex = 0; fieldIndex < fieldCount; fieldIndex++)
        {
            if (bytes.Length - offset < 7)
                throw new InvalidOperationException("Generated new-fixture result field header was truncated.");
            var fieldId = BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
            var kind = bytes[offset + 2];
            var fieldLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 3)..]);
            offset += 7;
            if (fieldLength > bytes.Length - offset)
                throw new InvalidOperationException("Generated new-fixture result field was truncated.");

            var value = bytes.Slice(offset, (int)fieldLength);
            switch (fieldId)
            {
                case 1 when kind == 2 && fieldLength == sizeof(int):
                    code = BinaryPrimitives.ReadInt32LittleEndian(value);
                    break;
                case 2 when kind == 3 && fieldLength == 1 && value[0] <= 1:
                    accepted = value[0] != 0;
                    break;
                case 9 when kind == 3 && fieldLength == 1 && value[0] <= 1:
                    audited = value[0] != 0;
                    break;
                default:
                    throw new InvalidOperationException("Generated new-fixture result field was invalid.");
            }
            offset += (int)fieldLength;
        }

        if (offset != bytes.Length || fieldCount is < 2 or > 3)
            throw new InvalidOperationException("Generated new-fixture result had an invalid field count.");
        return (code, accepted, audited);
    }

    private static byte[] CreateNlps(ReadOnlySpan<byte> schemaHash, bool isNew)
    {
        var fields = isNew
            ? new (ushort Id, byte Kind, byte Flags)[] { (1, 1, 0), (2, 2, 0), (7, 1, 2) }
            : new (ushort Id, byte Kind, byte Flags)[] { (1, 1, 0), (2, 2, 0) };
        var bytes = new byte[88 + fields.Length * 4];
        "NLPS"u8.CopyTo(bytes);
        WriteUInt16(bytes, 4, 1);
        WriteUInt16(bytes, 6, 88);
        WriteUInt32(bytes, 8, (uint)bytes.Length);
        WriteUInt32(bytes, 12, 1);
        WriteUInt32(bytes, 16, 5);
        WriteUInt32(bytes, 20, 1);
        WriteUInt32(bytes, 24, isNew ? 1u : 0u);
        WriteUInt32(bytes, 28, FeatureBits);
        WriteUInt32(bytes, 32, FeatureBits);
        WriteUInt32(bytes, 36, 17);
        WriteUInt32(bytes, 40, 1);
        WriteUInt32(bytes, 44, 21);
        WriteUInt32(bytes, 48, 1);
        WriteUInt16(bytes, 52, (ushort)fields.Length);
        WriteUInt16(bytes, 54, 4);
        schemaHash.CopyTo(bytes.AsSpan(56, 32));
        for (var index = 0; index < fields.Length; index++)
        {
            var offset = 88 + index * 4;
            WriteUInt16(bytes, offset, fields[index].Id);
            bytes[offset + 2] = fields[index].Kind;
            bytes[offset + 3] = fields[index].Flags;
        }
        return bytes;
    }

    private static byte[] CreateNlmr(
        ReadOnlySpan<byte> localSchemaHash,
        ReadOnlySpan<byte> peerSchemaHash,
        bool isNew,
        ulong endpointToken)
    {
        var fields = isNew
            ? new (ushort Id, byte Kind, byte Flags)[] { (1, 2, 0), (2, 3, 0), (9, 3, 2) }
            : new (ushort Id, byte Kind, byte Flags)[] { (1, 2, 0), (2, 3, 0) };
        var bytes = new byte[136 + fields.Length * 4];
        "NLMR"u8.CopyTo(bytes);
        WriteUInt16(bytes, 4, 1);
        WriteUInt16(bytes, 6, 136);
        WriteUInt32(bytes, 8, (uint)bytes.Length);
        WriteUInt32(bytes, 12, 1);
        WriteUInt64(bytes, 16, endpointToken);
        WriteUInt32(bytes, 24, 1);
        WriteUInt32(bytes, 28, 1);
        WriteUInt32(bytes, 32, 5);
        WriteUInt32(bytes, 36, 1);
        WriteUInt32(bytes, 40, isNew ? 1u : 0u);
        WriteUInt32(bytes, 44, FeatureBits);
        WriteUInt32(bytes, 48, FeatureBits);
        WriteUInt32(bytes, 52, 17);
        WriteUInt32(bytes, 56, 1);
        WriteUInt32(bytes, 60, 22);
        WriteUInt32(bytes, 64, 1);
        WriteUInt16(bytes, 68, (ushort)fields.Length);
        WriteUInt16(bytes, 70, 4);
        localSchemaHash.CopyTo(bytes.AsSpan(72, 32));
        peerSchemaHash.CopyTo(bytes.AsSpan(104, 32));
        for (var index = 0; index < fields.Length; index++)
        {
            var offset = 136 + index * 4;
            WriteUInt16(bytes, offset, fields[index].Id);
            bytes[offset + 2] = fields[index].Kind;
            bytes[offset + 3] = fields[index].Flags;
        }
        return bytes;
    }

    private static void WriteUInt16(byte[] bytes, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);

    private static void WriteUInt32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);

    private static void WriteUInt64(byte[] bytes, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);

    internal sealed class AsyncCallbackV1SmokeRegistry(
        LiftAsyncCallbackV1HostDescriptor descriptor,
        bool failRouteDisposal = false) : ILiftAsyncCallbackV1HostRegistry
    {
        public LiftAsyncCallbackV1HostDescriptor Descriptor { get; } = descriptor;
        public LiftAsyncCallbackV1ManagedRouteHandler? Handler { get; private set; }
        public int RegistrationCount { get; private set; }

        public IDisposable RegisterManagedRoute(
            uint serviceId,
            uint methodId,
            LiftAsyncCallbackV1ManagedRouteHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (serviceId != 17 || methodId != 1 || Handler is not null)
                throw new InvalidOperationException("The smoke registry route was invalid or already installed.");

            Handler = handler;
            RegistrationCount++;
            return new RouteRegistration(this, handler, failRouteDisposal);
        }

        public ValueTask<ReadOnlyMemory<byte>> InvokeAsync(
            ReadOnlyMemory<byte> payload,
            LiftInvocationContext invocationContext,
            CancellationToken cancellationToken) =>
            (Handler ?? throw new InvalidOperationException("No generated smoke route is installed."))(
                payload, invocationContext, cancellationToken);

        private void Remove(LiftAsyncCallbackV1ManagedRouteHandler handler)
        {
            if (ReferenceEquals(Handler, handler))
                Handler = null;
        }

        private sealed class RouteRegistration(
            AsyncCallbackV1SmokeRegistry registry,
            LiftAsyncCallbackV1ManagedRouteHandler handler,
            bool failDisposal) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (failDisposal)
                    throw new InvalidOperationException("Injected smoke route rollback failure.");
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    registry.Remove(handler);
            }
        }
    }

    internal sealed class AsyncCallbackV1SmokeInvoker(bool confirmationAccepted)
        : ILiftAsyncCallbackV1NativeServiceInvoker
    {
        public TaskCompletionSource<bool> CallEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<LiftNativeReply>? DeferredReply { get; set; }
        public int CallCount { get; private set; }
        public uint LastServiceId { get; private set; }
        public uint LastMethodId { get; private set; }
        public string? LastPrompt { get; private set; }
        public LiftInvocationContext LastInvocationContext { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }

        public ValueTask<LiftNativeReply> InvokeAsync(
            uint serviceId,
            uint methodId,
            ReadOnlyMemory<byte> payload,
            ulong correlationToken,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<LiftNativeReply>(
                new InvalidOperationException("AsyncCallbackV1 calls must preserve invocation context."));

        public ValueTask<LiftNativeReply> InvokeAsync(
            uint serviceId,
            uint methodId,
            ReadOnlyMemory<byte> payload,
            LiftInvocationContext invocationContext,
            CancellationToken cancellationToken)
        {
            if (serviceId != 30 || methodId != 1 ||
                invocationContext.EndpointToken == 0 ||
                invocationContext.RegistrationToken == 0 ||
                invocationContext.RegistrationGeneration == 0 ||
                invocationContext.OperationToken == 0 ||
                invocationContext.CorrelationToken == 0 ||
                cancellationToken != invocationContext.CancellationToken)
                throw new InvalidOperationException("The generated callback lost V2 invocation identity.");

            CallCount++;
            LastServiceId = serviceId;
            LastMethodId = methodId;
            LastPrompt = DecodePrompt(payload.Span);
            LastInvocationContext = invocationContext;
            LastCancellationToken = cancellationToken;
            CallEntered.TrySetResult(true);
            if (DeferredReply is { } deferredReply)
                return new ValueTask<LiftNativeReply>(deferredReply.Task.WaitAsync(cancellationToken));

            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new LiftNativeReply(
                LiftNativeReplyKind.Result,
                new byte[] { confirmationAccepted ? (byte)1 : (byte)0 },
                ReadOnlyMemory<byte>.Empty));
        }

        private static string DecodePrompt(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < 19 ||
                !bytes[..4].SequenceEqual("NLF1"u8) ||
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != 1 ||
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]) != 1 ||
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]) != 1 ||
                bytes[14] != 1)
                throw new InvalidOperationException("Generated confirmation request was not canonical NLF1.");

            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[15..]);
            if (length != bytes.Length - 19 ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != bytes.Length - 12)
                throw new InvalidOperationException("Generated confirmation request length was invalid.");
            return StrictUtf8.GetString(bytes[19..]);
        }
    }
}
