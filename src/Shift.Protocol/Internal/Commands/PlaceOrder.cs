using System.Buffers.Binary;

namespace Shift.Protocol.Internal.Commands;

public readonly record struct PlaceOrder(
    long PairId,
    long OrderId,
    OrderSide Side,
    long PriceTicks,
    long Quantity,
    OrderType OrderType);

public static class PlaceOrderCodec
{
    private const int EncodedLength = (4 * sizeof(long)) + (2 * sizeof(byte));

    public static int Encode(PlaceOrder command, Span<byte> destination)
    {
        if (command.PairId <= 0
            || command.OrderId <= 0
            || !Enum.IsDefined(command.Side)
            || command.PriceTicks <= 0
            || command.Quantity <= 0
            || !Enum.IsDefined(command.OrderType))
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        if (destination.Length < EncodedLength)
        {
            throw new ArgumentException("Destination is too small for the encoded command.", nameof(destination));
        }

        BinaryPrimitives.WriteInt64BigEndian(destination, command.PairId);
        BinaryPrimitives.WriteInt64BigEndian(destination[8..], command.OrderId);
        destination[16] = (byte)command.Side;
        BinaryPrimitives.WriteInt64BigEndian(destination[17..], command.PriceTicks);
        BinaryPrimitives.WriteInt64BigEndian(destination[25..], command.Quantity);
        destination[33] = (byte)command.OrderType;
        return EncodedLength;
    }

    public static bool TryDecode(ReadOnlySpan<byte> source, out PlaceOrder command)
    {
        if (source.Length != EncodedLength)
        {
            command = default;
            return false;
        }

        long pairId = BinaryPrimitives.ReadInt64BigEndian(source);
        long orderId = BinaryPrimitives.ReadInt64BigEndian(source[8..]);
        var side = (OrderSide)source[16];
        long priceTicks = BinaryPrimitives.ReadInt64BigEndian(source[17..]);
        long quantity = BinaryPrimitives.ReadInt64BigEndian(source[25..]);
        var orderType = (OrderType)source[33];
        if (pairId <= 0
            || orderId <= 0
            || !Enum.IsDefined(side)
            || priceTicks <= 0
            || quantity <= 0
            || !Enum.IsDefined(orderType))
        {
            command = default;
            return false;
        }

        command = new PlaceOrder(pairId, orderId, side, priceTicks, quantity, orderType);
        return true;
    }
}
