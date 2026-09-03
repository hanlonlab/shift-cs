namespace Shift.Protocol.Internal.Commands;

public readonly record struct StartNewSession(Guid SessionId);

public static class StartNewSessionCodec
{
    private const int EncodedLength = 16;

    public static int Encode(StartNewSession command, Span<byte> destination)
    {
        if (command.SessionId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        if (destination.Length < EncodedLength)
        {
            throw new ArgumentException("Destination is too small for the encoded command.", nameof(destination));
        }

        command.SessionId.TryWriteBytes(destination, bigEndian: true, out _);
        return EncodedLength;
    }

    public static bool TryDecode(ReadOnlySpan<byte> source, out StartNewSession command)
    {
        if (source.Length != EncodedLength)
        {
            command = default;
            return false;
        }

        Guid sessionId = new(source, bigEndian: true);
        if (sessionId == Guid.Empty)
        {
            command = default;
            return false;
        }

        command = new StartNewSession(sessionId);
        return true;
    }
}
