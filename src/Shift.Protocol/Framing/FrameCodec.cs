using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;

namespace Shift.Protocol.Framing;

public static class FrameCodec
{
    public const byte CurrentVersion = 1;

    private const int VersionOffset = sizeof(uint);
    private const int MessageTypeOffset = VersionOffset + sizeof(byte);
    private const int MessageIdOffset = MessageTypeOffset + sizeof(ushort);
    private const int MessageIdSize = 16;
    private const int SequenceIdOffset = MessageIdOffset + MessageIdSize;

    public const int HeaderSize = SequenceIdOffset + sizeof(long);
    public const int ChecksumSize = sizeof(uint);
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
        frame[VersionOffset] = CurrentVersion;
        BinaryPrimitives.WriteUInt16BigEndian(frame[MessageTypeOffset..], (ushort)messageType);
        messageId.TryWriteBytes(frame.Slice(MessageIdOffset, MessageIdSize), bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(frame[SequenceIdOffset..], sequenceId);

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

        OperationStatus prefixStatus = TryReadFramePrefix(source, out uint totalLength, out MessageType messageType);
        if (prefixStatus != OperationStatus.Done)
        {
            return prefixStatus;
        }

        if (source.Length < (int)totalLength)
        {
            return OperationStatus.NeedMoreData;
        }

        ReadOnlySpan<byte> frame = source[..(int)totalLength];
        uint encodedChecksum = BinaryPrimitives.ReadUInt32BigEndian(frame[^ChecksumSize..]);
        uint computedChecksum = ComputeCrc32C(frame[..^ChecksumSize]);
        if (encodedChecksum != computedChecksum)
        {
            return OperationStatus.InvalidData;
        }

        header = new FrameHeader(
            totalLength,
            CurrentVersion,
            messageType,
            new Guid(frame.Slice(MessageIdOffset, MessageIdSize), bigEndian: true),
            BinaryPrimitives.ReadInt64BigEndian(frame[SequenceIdOffset..]));
        payload = frame.Slice(HeaderSize, frame.Length - MinimumFrameSize);
        bytesConsumed = frame.Length;
        return OperationStatus.Done;
    }

    private static OperationStatus TryReadFramePrefix(
        ReadOnlySpan<byte> source,
        out uint totalLength,
        out MessageType messageType)
    {
        totalLength = 0;
        messageType = default;

        if (source.Length < VersionOffset)
        {
            return OperationStatus.NeedMoreData;
        }

        totalLength = BinaryPrimitives.ReadUInt32BigEndian(source);
        if (totalLength is < MinimumFrameSize or > int.MaxValue)
        {
            return OperationStatus.InvalidData;
        }

        if (source.Length < MessageTypeOffset)
        {
            return OperationStatus.NeedMoreData;
        }

        if (source[VersionOffset] != CurrentVersion)
        {
            return OperationStatus.InvalidData;
        }

        if (source.Length < MessageIdOffset)
        {
            return OperationStatus.NeedMoreData;
        }

        ushort encodedMessageType = BinaryPrimitives.ReadUInt16BigEndian(source[MessageTypeOffset..]);
        if (encodedMessageType == 0)
        {
            return OperationStatus.InvalidData;
        }

        messageType = (MessageType)encodedMessageType;
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
