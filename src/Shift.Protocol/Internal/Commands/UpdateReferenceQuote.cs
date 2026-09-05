using System.Buffers.Binary;

namespace Shift.Protocol.Internal.Commands;

public readonly record struct UpdateReferenceQuote(
    long PairId,
    ReferenceLevel Bid,
    ReferenceLevel Ask);

public static class UpdateReferenceQuoteCodec
{
    private const int EncodedLength = 5 * sizeof(long);

    public static int Encode(UpdateReferenceQuote command, Span<byte> destination)
    {
        if (!IsValid(command))
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        if (destination.Length < EncodedLength)
        {
            throw new ArgumentException("Destination is too small for the encoded command.", nameof(destination));
        }

        BinaryPrimitives.WriteInt64BigEndian(destination, command.PairId);
        BinaryPrimitives.WriteInt64BigEndian(destination[8..], command.Bid.PriceTicks);
        BinaryPrimitives.WriteInt64BigEndian(destination[16..], command.Bid.Quantity);
        BinaryPrimitives.WriteInt64BigEndian(destination[24..], command.Ask.PriceTicks);
        BinaryPrimitives.WriteInt64BigEndian(destination[32..], command.Ask.Quantity);
        return EncodedLength;
    }

    public static bool TryDecode(ReadOnlySpan<byte> source, out UpdateReferenceQuote command)
    {
        if (source.Length != EncodedLength)
        {
            command = default;
            return false;
        }

        var decoded = new UpdateReferenceQuote(
            BinaryPrimitives.ReadInt64BigEndian(source),
            new ReferenceLevel(
                BinaryPrimitives.ReadInt64BigEndian(source[8..]),
                BinaryPrimitives.ReadInt64BigEndian(source[16..])),
            new ReferenceLevel(
                BinaryPrimitives.ReadInt64BigEndian(source[24..]),
                BinaryPrimitives.ReadInt64BigEndian(source[32..])));
        if (!IsValid(decoded))
        {
            command = default;
            return false;
        }

        command = decoded;
        return true;
    }

    private static bool IsValid(UpdateReferenceQuote command)
    {
        return command.PairId > 0
            && IsValidLevel(command.Bid)
            && IsValidLevel(command.Ask)
            && (command.Bid.Quantity == 0 || command.Ask.Quantity == 0
                || command.Bid.PriceTicks < command.Ask.PriceTicks);
    }

    private static bool IsValidLevel(ReferenceLevel level)
    {
        return level == default || (level.PriceTicks > 0 && level.Quantity > 0);
    }
}
