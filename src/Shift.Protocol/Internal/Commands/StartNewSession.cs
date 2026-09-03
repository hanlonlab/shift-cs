namespace Shift.Protocol.Internal.Commands;

public readonly record struct StartNewSession;

public static class StartNewSessionCodec
{
    public static int Encode(StartNewSession command, Span<byte> destination) => 0;

    public static bool TryDecode(ReadOnlySpan<byte> source, out StartNewSession command)
    {
        if (!source.IsEmpty)
        {
            command = default;
            return false;
        }

        command = new StartNewSession();
        return true;
    }
}
