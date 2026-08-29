using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;

namespace Shift.Protocol.Framing;

public static class FrameCodec
{
    public const byte CurrentVersion = 1;
    public const int HeaderSize = 31;
    public const int ChecksumSize = 4;
    public const int MinimumFrameSize = HeaderSize + ChecksumSize;

    public static int Encode(
        MessageType messageType,
        Guid messageId,
        long sequenceId,
        ReadOnlySpan<byte> payload,
        Span<byte> destination)
    {
        if (messageType == default)
        {
            throw new ArgumentOutOfRangeException(nameof(messageType));
        }

        if (payload.Length > int.MaxValue - MinimumFrameSize)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        int totalLength = MinimumFrameSize + payload.Length;
        if (destination.Length < totalLength)
        {
            throw new ArgumentException("Destination is too small for the encoded frame.", nameof(destination));
        }

        Span<byte> frame = destination[..totalLength];
        payload.CopyTo(frame.Slice(HeaderSize, payload.Length));
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)totalLength);
        frame[4] = CurrentVersion;
        BinaryPrimitives.WriteUInt16BigEndian(frame[5..], (ushort)messageType);
        messageId.TryWriteBytes(frame.Slice(7, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(frame[23..], sequenceId);

        uint checksum = ComputeCrc32C(frame[..^ChecksumSize]);
        BinaryPrimitives.WriteUInt32BigEndian(frame[^ChecksumSize..], checksum);
        return totalLength;
    }

    public static OperationStatus TryDecode(
        ReadOnlySpan<byte> source,
        out FrameHeader header,
        out ReadOnlySpan<byte> payload,
        out int bytesConsumed)
    {
        header = default;
        payload = default;
        bytesConsumed = 0;

        if (source.Length < sizeof(uint))
        {
            return OperationStatus.NeedMoreData;
        }

        uint totalLength = BinaryPrimitives.ReadUInt32BigEndian(source);
        if (totalLength is < MinimumFrameSize or > int.MaxValue)
        {
            return OperationStatus.InvalidData;
        }

        if (source.Length < 5)
        {
            return OperationStatus.NeedMoreData;
        }

        if (source[4] != CurrentVersion)
        {
            return OperationStatus.InvalidData;
        }

        if (source.Length < 7)
        {
            return OperationStatus.NeedMoreData;
        }

        ushort messageType = BinaryPrimitives.ReadUInt16BigEndian(source[5..]);
        if (messageType == 0)
        {
            return OperationStatus.InvalidData;
        }

        if (source.Length < (int)totalLength)
        {
            return OperationStatus.NeedMoreData;
        }

        ReadOnlySpan<byte> frame = source[..(int)totalLength];
        uint expectedChecksum = BinaryPrimitives.ReadUInt32BigEndian(frame[^ChecksumSize..]);
        if (expectedChecksum != ComputeCrc32C(frame[..^ChecksumSize]))
        {
            return OperationStatus.InvalidData;
        }

        header = new FrameHeader(
            totalLength,
            frame[4],
            (MessageType)messageType,
            new Guid(frame.Slice(7, 16), bigEndian: true),
            BinaryPrimitives.ReadInt64BigEndian(frame[23..]));
        payload = frame.Slice(HeaderSize, frame.Length - MinimumFrameSize);
        bytesConsumed = frame.Length;
        return OperationStatus.Done;
    }

    private static uint ComputeCrc32C(ReadOnlySpan<byte> source)
    {
        uint checksum = uint.MaxValue;
        foreach (byte value in source)
        {
            checksum = BitOperations.Crc32C(checksum, value);
        }

        return ~checksum;
    }
}
