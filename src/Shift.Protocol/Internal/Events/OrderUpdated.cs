using System.Buffers.Binary;

namespace Shift.Protocol.Internal.Events;

public readonly record struct OrderUpdated(
    long PairId,
    long OrderId,
    long RemainingQuantity,
    long CanceledQuantity,
    RejectionReason RejectionReason,
    CancellationReason CancellationReason);

public static class OrderUpdatedCodec
{
    private const int EncodedLength = (4 * sizeof(long)) + (2 * sizeof(byte));

    public static int Encode(OrderUpdated message, Span<byte> destination)
    {
        if (!IsValid(message))
        {
            throw new ArgumentOutOfRangeException(nameof(message));
        }

        if (destination.Length < EncodedLength)
        {
            throw new ArgumentException("Destination is too small for the encoded event.", nameof(destination));
        }

        BinaryPrimitives.WriteInt64BigEndian(destination, message.PairId);
        BinaryPrimitives.WriteInt64BigEndian(destination[8..], message.OrderId);
        BinaryPrimitives.WriteInt64BigEndian(destination[16..], message.RemainingQuantity);
        BinaryPrimitives.WriteInt64BigEndian(destination[24..], message.CanceledQuantity);
        destination[32] = (byte)message.RejectionReason;
        destination[33] = (byte)message.CancellationReason;
        return EncodedLength;
    }

    public static bool TryDecode(ReadOnlySpan<byte> source, out OrderUpdated message)
    {
        if (source.Length != EncodedLength)
        {
            message = default;
            return false;
        }

        long pairId = BinaryPrimitives.ReadInt64BigEndian(source);
        long orderId = BinaryPrimitives.ReadInt64BigEndian(source[8..]);
        long remainingQuantity = BinaryPrimitives.ReadInt64BigEndian(source[16..]);
        long canceledQuantity = BinaryPrimitives.ReadInt64BigEndian(source[24..]);
        var rejectionReason = (RejectionReason)source[32];
        var cancellationReason = (CancellationReason)source[33];
        var decoded = new OrderUpdated(
            pairId,
            orderId,
            remainingQuantity,
            canceledQuantity,
            rejectionReason,
            cancellationReason);
        if (!IsValid(decoded))
        {
            message = default;
            return false;
        }

        message = decoded;
        return true;
    }

    private static bool IsValid(OrderUpdated message)
    {
        return message.PairId > 0
            && message.OrderId > 0
            && message.RemainingQuantity >= 0
            && message.CanceledQuantity >= 0
            && Enum.IsDefined(message.RejectionReason)
            && Enum.IsDefined(message.CancellationReason);
    }
}
