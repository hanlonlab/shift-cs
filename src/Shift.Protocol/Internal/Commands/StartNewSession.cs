namespace Shift.Protocol.Internal.Commands;

public readonly record struct StartNewSession(Guid SimulationId);

public static class StartNewSessionCodec
{
    private const int EncodedLength = 16;

    public static int Encode(StartNewSession command, Span<byte> destination)
    {
        if (destination.Length < EncodedLength)
        {
            throw new ArgumentException("Destination is too small for the encoded command.", nameof(destination));
        }

        command.SimulationId.TryWriteBytes(destination, bigEndian: true, out _);
        return EncodedLength;
    }

    public static bool TryDecode(ReadOnlySpan<byte> source, out StartNewSession command)
    {
        if (source.Length != EncodedLength)
        {
            command = default;
            return false;
        }

        command = new StartNewSession(new Guid(source, bigEndian: true));
        return true;
    }
}
