namespace Shift.Protocol.Internal.Commands;

public static class EndCurrentSessionCodec
{
    public static bool IsValidPayload(ReadOnlySpan<byte> payload) => payload.IsEmpty;
}
