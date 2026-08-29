using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;

namespace Shift.Protocol.Framing;

/// <summary>
/// Encodes and decodes version 1 internal frames.
/// </summary>
/// <remarks>
/// <code>
/// [frame length:4][version:1][message type:2][message ID:16][sequence ID:8][payload:N][CRC-32C:4]
/// </code>
/// Multibyte values are big-endian. Frame length includes the entire frame, and CRC-32C covers
/// every preceding byte. Payload length is frame length minus <see cref="MinimumFrameSize"/>.
/// </remarks>
public static class FrameCodec
{
    public const byte CurrentVersion = 1;

    private const int FrameLengthFieldSize = sizeof(uint);
    private const int VersionOffset = FrameLengthFieldSize;
    private const int VersionFieldSize = sizeof(byte);
    private const int MessageTypeOffset = VersionOffset + VersionFieldSize;
    private const int MessageTypeFieldSize = sizeof(ushort);
    private const int MessageIdOffset = MessageTypeOffset + MessageTypeFieldSize;
    private const int MessageIdFieldSize = 16;
    private const int SequenceIdOffset = MessageIdOffset + MessageIdFieldSize;
    private const int SequenceIdFieldSize = sizeof(long);

    public const int HeaderSize = SequenceIdOffset + SequenceIdFieldSize;
    public const int ChecksumFieldSize = sizeof(uint);
    public const int MinimumFrameSize = HeaderSize + ChecksumFieldSize;

    /// <summary>
    /// Encodes one frame into <paramref name="destination"/> and returns the number of bytes written.
    /// </summary>
    /// <remarks>Payload and destination may overlap.</remarks>
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

        int frameLength = MinimumFrameSize + payload.Length;
        if (destination.Length < frameLength)
        {
            throw new ArgumentException("Destination is too small for the encoded frame.", nameof(destination));
        }

        Span<byte> frame = destination[..frameLength];
        payload.CopyTo(frame.Slice(HeaderSize, payload.Length));

        BinaryPrimitives.WriteUInt32BigEndian(
            frame[..FrameLengthFieldSize],
            (uint)frameLength);
        frame[VersionOffset] = CurrentVersion;
        BinaryPrimitives.WriteUInt16BigEndian(
            frame.Slice(MessageTypeOffset, MessageTypeFieldSize),
            (ushort)messageType);
        messageId.TryWriteBytes(frame.Slice(MessageIdOffset, MessageIdFieldSize), bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(
            frame.Slice(SequenceIdOffset, SequenceIdFieldSize),
            sequenceId);

        int checksumOffset = frameLength - ChecksumFieldSize;
        uint checksum = ComputeCrc32C(frame[..checksumOffset]);
        BinaryPrimitives.WriteUInt32BigEndian(
            frame.Slice(checksumOffset, ChecksumFieldSize),
            checksum);
        return frameLength;
    }

    /// <summary>
    /// Attempts to decode one complete frame from <paramref name="source"/>.
    /// </summary>
    /// <remarks>
    /// Source must contain exactly one frame. On success, <paramref name="payload"/> references
    /// the original source buffer.
    /// </remarks>
    public static OperationStatus TryDecode(
        ReadOnlySpan<byte> source,
        out FrameHeader header,
        out ReadOnlySpan<byte> payload)
    {
        header = default;
        payload = default;

        if (source.Length < FrameLengthFieldSize)
        {
            return OperationStatus.NeedMoreData;
        }

        uint declaredFrameLength = BinaryPrimitives.ReadUInt32BigEndian(source[..FrameLengthFieldSize]);
        if (declaredFrameLength is < MinimumFrameSize or > int.MaxValue)
        {
            return OperationStatus.InvalidData;
        }

        int frameLength = (int)declaredFrameLength;
        if (source.Length != frameLength)
        {
            return source.Length < frameLength
                ? OperationStatus.NeedMoreData
                : OperationStatus.InvalidData;
        }

        if (source[VersionOffset] != CurrentVersion)
        {
            return OperationStatus.InvalidData;
        }

        ushort encodedMessageType = BinaryPrimitives.ReadUInt16BigEndian(
            source.Slice(MessageTypeOffset, MessageTypeFieldSize));
        if (encodedMessageType == 0)
        {
            return OperationStatus.InvalidData;
        }

        int checksumOffset = frameLength - ChecksumFieldSize;
        uint encodedChecksum = BinaryPrimitives.ReadUInt32BigEndian(
            source.Slice(checksumOffset, ChecksumFieldSize));
        uint computedChecksum = ComputeCrc32C(source[..checksumOffset]);
        if (encodedChecksum != computedChecksum)
        {
            return OperationStatus.InvalidData;
        }

        header = new FrameHeader(
            declaredFrameLength,
            CurrentVersion,
            (MessageType)encodedMessageType,
            new Guid(source.Slice(MessageIdOffset, MessageIdFieldSize), bigEndian: true),
            BinaryPrimitives.ReadInt64BigEndian(source.Slice(SequenceIdOffset, SequenceIdFieldSize)));
        int payloadLength = frameLength - MinimumFrameSize;
        payload = source.Slice(HeaderSize, payloadLength);
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
