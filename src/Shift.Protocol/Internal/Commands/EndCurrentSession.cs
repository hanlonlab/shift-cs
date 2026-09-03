namespace Shift.Protocol.Internal.Commands;

public readonly record struct EndCurrentSession;

public static class EndCurrentSessionCodec
{
    public static int Encode(EndCurrentSession command, Span<byte> destination) => 0;

    public static bool TryDecode(ReadOnlySpan<byte> source, out EndCurrentSession command)
    {
        if (!source.IsEmpty)
        {
            command = default;
            return false;
        }

        command = new EndCurrentSession();
        return true;
    }
}
