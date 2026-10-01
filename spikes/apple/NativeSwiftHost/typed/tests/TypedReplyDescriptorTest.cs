using NetLift.ManagedBridge.Typed;

unsafe
{
    byte sentinel = 0;
    var reply = new LiftNativeReply
    {
        StructSize = (uint)sizeof(LiftNativeReply),
        ProtocolVersion = 1,
        Outcome = 0,
        Accepted = 1,
        Diagnostic = new LiftBytesView
        {
            Data = &sentinel,
            Length = 0
        }
    };
    var status = new LiftStatus();
    var result = ManagedEntryPoints.ReplyTyped(1, 1, &reply, &status);
    if (result != 10 || status.Code != 10)
    {
        throw new InvalidOperationException(
            $"A zero-length diagnostic with a non-null pointer was rejected before request lookup (return={result}, status={status.Code}).");
    }

    reply.Diagnostic = new LiftBytesView
    {
        Data = null,
        Length = 1
    };
    status = new LiftStatus();
    result = ManagedEntryPoints.ReplyTyped(1, 1, &reply, &status);
    if (result != 1 || status.Code != 1)
    {
        throw new InvalidOperationException(
            $"A non-empty diagnostic with a null pointer was not rejected (return={result}, status={status.Code}).");
    }

    byte[] malformedUtf8 = { 0xed, 0xa0, 0x80 };
    fixed (byte* data = malformedUtf8)
    {
        reply = new LiftNativeReply
        {
            StructSize = (uint)sizeof(LiftNativeReply),
            ProtocolVersion = 1,
            Outcome = 5,
            Accepted = 0,
            Diagnostic = new LiftBytesView { Data = data, Length = (uint)malformedUtf8.Length }
        };
        status = new LiftStatus();
        result = ManagedEntryPoints.ReplyTyped(1, 1, &reply, &status);
        if (result != 7 || status.Code != 7)
        {
            throw new InvalidOperationException(
                $"Malformed UTF-8 was not rejected explicitly (return={result}, status={status.Code}).");
        }
    }

    byte[] boundaryUtf8 = new byte[4096];
    Array.Fill(boundaryUtf8, (byte)'x');
    fixed (byte* data = boundaryUtf8)
    {
        reply = new LiftNativeReply
        {
            StructSize = (uint)sizeof(LiftNativeReply),
            ProtocolVersion = 1,
            Outcome = 5,
            Accepted = 0,
            Diagnostic = new LiftBytesView { Data = data, Length = (uint)boundaryUtf8.Length }
        };
        status = new LiftStatus();
        result = ManagedEntryPoints.ReplyTyped(1, 1, &reply, &status);
        if (result != 10 || status.Code != 10)
        {
            throw new InvalidOperationException(
                $"A valid 4096-byte diagnostic did not pass decoding before request lookup (return={result}, status={status.Code}).");
        }
    }

    byte[] oversizedUtf8 = new byte[4097];
    Array.Fill(oversizedUtf8, (byte)'x');
    fixed (byte* data = oversizedUtf8)
    {
        reply = new LiftNativeReply
        {
            StructSize = (uint)sizeof(LiftNativeReply),
            ProtocolVersion = 1,
            Outcome = 5,
            Accepted = 0,
            Diagnostic = new LiftBytesView { Data = data, Length = (uint)oversizedUtf8.Length }
        };
        status = new LiftStatus();
        result = ManagedEntryPoints.ReplyTyped(1, 1, &reply, &status);
        if (result != 6 || status.Code != 6)
        {
            throw new InvalidOperationException(
                $"An oversized diagnostic was not rejected (return={result}, status={status.Code}).");
        }
    }
}

Console.WriteLine("PASS managed ReplyTyped validates diagnostic pointers, strict UTF-8, and the 4096-byte limit.");
