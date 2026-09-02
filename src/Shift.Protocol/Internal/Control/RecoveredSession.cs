using System.Buffers.Binary;

namespace Shift.Protocol.Internal.Control;

public enum RecoveredSessionStatus : byte
{
    Idle = 0,
    Active = 1,
    Ended = 2,
}

public readonly record struct RecoveredProducerWatermark(
    ushort ProducerId,
    ulong ProducerSequence);

public readonly record struct RecoveredSession(
    Guid SessionId,
    long CommittedThrough,
    RecoveredSessionStatus Status,
    RecoveredProducerWatermark[] Producers);

public static class RecoveredSessionCodec
{
    public const int PrefixSize = 16 + sizeof(long) + sizeof(byte) + sizeof(ushort);
    public const int WatermarkSize = sizeof(ushort) + sizeof(ulong);

    public static int GetEncodedLength(int producerCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(producerCount);
        if (producerCount > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(producerCount));
        }

        return PrefixSize + (producerCount * WatermarkSize);
    }

    public static int Encode(RecoveredSession session, Span<byte> destination)
    {
        RecoveredProducerWatermark[] producers = session.Producers ?? [];
        int encodedLength = GetEncodedLength(producers.Length);
        if (destination.Length < encodedLength)
        {
            throw new ArgumentException("Destination is too small for the encoded command.", nameof(destination));
        }

        if (session.Status > RecoveredSessionStatus.Ended)
        {
            throw new ArgumentOutOfRangeException(nameof(session));
        }

        session.SessionId.TryWriteBytes(destination[..16], bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(destination.Slice(16, sizeof(long)), session.CommittedThrough);
        destination[24] = (byte)session.Status;
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(25, sizeof(ushort)), (ushort)producers.Length);

        int offset = PrefixSize;
        foreach (RecoveredProducerWatermark watermark in producers)
        {
            BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(offset, sizeof(ushort)), watermark.ProducerId);
            BinaryPrimitives.WriteUInt64BigEndian(
                destination.Slice(offset + sizeof(ushort), sizeof(ulong)),
                watermark.ProducerSequence);
            offset += WatermarkSize;
        }

        return encodedLength;
    }

    public static bool TryDecode(ReadOnlySpan<byte> source, out RecoveredSession session)
    {
        session = default;
        if (source.Length < PrefixSize)
        {
            return false;
        }

        ushort producerCount = BinaryPrimitives.ReadUInt16BigEndian(source.Slice(25, sizeof(ushort)));
        if (source.Length != PrefixSize + (producerCount * WatermarkSize))
        {
            return false;
        }

        byte encodedStatus = source[24];
        if (encodedStatus > (byte)RecoveredSessionStatus.Ended)
        {
            return false;
        }

        RecoveredProducerWatermark[] producers = producerCount == 0
            ? []
            : new RecoveredProducerWatermark[producerCount];
        int offset = PrefixSize;
        for (int index = 0; index < producerCount; index++)
        {
            producers[index] = new RecoveredProducerWatermark(
                BinaryPrimitives.ReadUInt16BigEndian(source.Slice(offset, sizeof(ushort))),
                BinaryPrimitives.ReadUInt64BigEndian(source.Slice(offset + sizeof(ushort), sizeof(ulong))));
            offset += WatermarkSize;
        }

        session = new RecoveredSession(
            new Guid(source[..16], bigEndian: true),
            BinaryPrimitives.ReadInt64BigEndian(source.Slice(16, sizeof(long))),
            (RecoveredSessionStatus)encodedStatus,
            producers);
        return true;
    }
}
