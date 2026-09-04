using System.Buffers.Binary;

namespace Shift.Protocol.Internal.Events;

public readonly record struct TradeExecuted(long PairId, Fill Fill);

public static class TradeExecutedCodec
{
    private const int EncodedLength = (4 * sizeof(long)) + sizeof(byte);

    public static int Encode(TradeExecuted message, Span<byte> destination)
    {
        Fill fill = message.Fill;
        if (message.PairId <= 0
            || fill.ParticipantOrderId <= 0
            || fill.PriceTicks <= 0
            || fill.Quantity <= 0
            || !Enum.IsDefined(fill.Role))
        {
            throw new ArgumentOutOfRangeException(nameof(message));
        }

        if (destination.Length < EncodedLength)
        {
            throw new ArgumentException("Destination is too small for the encoded event.", nameof(destination));
        }

        BinaryPrimitives.WriteInt64BigEndian(destination, message.PairId);
        BinaryPrimitives.WriteInt64BigEndian(destination[8..], fill.ParticipantOrderId);
        BinaryPrimitives.WriteInt64BigEndian(destination[16..], fill.PriceTicks);
        BinaryPrimitives.WriteInt64BigEndian(destination[24..], fill.Quantity);
        destination[32] = (byte)fill.Role;
        return EncodedLength;
    }

    public static bool TryDecode(ReadOnlySpan<byte> source, out TradeExecuted message)
    {
        if (source.Length != EncodedLength)
        {
            message = default;
            return false;
        }

        long pairId = BinaryPrimitives.ReadInt64BigEndian(source);
        long participantOrderId = BinaryPrimitives.ReadInt64BigEndian(source[8..]);
        long priceTicks = BinaryPrimitives.ReadInt64BigEndian(source[16..]);
        long quantity = BinaryPrimitives.ReadInt64BigEndian(source[24..]);
        var role = (FillRole)source[32];
        if (pairId <= 0
            || participantOrderId <= 0
            || priceTicks <= 0
            || quantity <= 0
            || !Enum.IsDefined(role))
        {
            message = default;
            return false;
        }

        message = new TradeExecuted(
            pairId,
            new Fill(participantOrderId, priceTicks, quantity, role));
        return true;
    }
}
