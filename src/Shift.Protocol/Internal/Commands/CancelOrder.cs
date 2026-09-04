using System.Buffers.Binary;

namespace Shift.Protocol.Internal.Commands;

public readonly record struct CancelOrder(long PairId, long OrderId);

public static class CancelOrderCodec
{
    private const int EncodedLength = 2 * sizeof(long);

    public static int Encode(CancelOrder command, Span<byte> destination)
    {
        if (command.PairId <= 0 || command.OrderId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        if (destination.Length < EncodedLength)
        {
            throw new ArgumentException("Destination is too small for the encoded command.", nameof(destination));
        }

        BinaryPrimitives.WriteInt64BigEndian(destination, command.PairId);
        BinaryPrimitives.WriteInt64BigEndian(destination[8..], command.OrderId);
        return EncodedLength;
    }

    public static bool TryDecode(ReadOnlySpan<byte> source, out CancelOrder command)
    {
        if (source.Length != EncodedLength)
        {
            command = default;
            return false;
        }

        long pairId = BinaryPrimitives.ReadInt64BigEndian(source);
        long orderId = BinaryPrimitives.ReadInt64BigEndian(source[8..]);
        if (pairId <= 0 || orderId <= 0)
        {
            command = default;
            return false;
        }

        command = new CancelOrder(pairId, orderId);
        return true;
    }
}
