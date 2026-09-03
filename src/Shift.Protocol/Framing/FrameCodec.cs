using System.Buffers;
using System.Buffers.Binary;

namespace Shift.Protocol.Framing;

/// <summary>
/// Encodes and decodes version 1 internal frames.
/// </summary>
/// <remarks>
/// <code>
/// [frame length:4][version:1][message type:2][session ID:16][producer ID:2][producer sequence:8][sequence ID:8][payload:N][CRC-32C:4]
/// </code>
/// Multibyte values are big-endian. Frame length includes the entire frame, and CRC-32C covers
/// every preceding byte. Payload length is frame length minus <see cref="MinimumFrameSize"/>.
/// Producer ID 0 is reserved for Archiver control frames.
/// </remarks>
public static class FrameCodec
{
    public const byte CurrentVersion = 1;
    public const ushort ControlProducerId = 0;
    public const int MaximumFrameSize = 2_048;

    private const int FrameLengthFieldSize = sizeof(uint);
    private const int VersionOffset = FrameLengthFieldSize;
    private const int VersionFieldSize = sizeof(byte);
    private const int MessageTypeOffset = VersionOffset + VersionFieldSize;
    private const int MessageTypeFieldSize = sizeof(ushort);
    private const int SessionIdOffset = MessageTypeOffset + MessageTypeFieldSize;
    private const int SessionIdFieldSize = 16;
    private const int ProducerIdOffset = SessionIdOffset + SessionIdFieldSize;
    private const int ProducerIdFieldSize = sizeof(ushort);
    private const int ProducerSequenceOffset = ProducerIdOffset + ProducerIdFieldSize;
    private const int ProducerSequenceFieldSize = sizeof(ulong);
    private const int SequenceIdOffset = ProducerSequenceOffset + ProducerSequenceFieldSize;
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
        Guid sessionId,
        ushort producerId,
        ulong producerSequence,
        long sequenceId,
        ReadOnlySpan<byte> payload,
        Span<byte> destination)
    {
        int frameLength = GetFrameLength(messageType, sessionId, payload.Length);
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
        sessionId.TryWriteBytes(
            frame.Slice(SessionIdOffset, SessionIdFieldSize),
            bigEndian: true,
            out _);
        BinaryPrimitives.WriteUInt16BigEndian(
            frame.Slice(ProducerIdOffset, ProducerIdFieldSize),
            producerId);
        BinaryPrimitives.WriteUInt64BigEndian(
            frame.Slice(ProducerSequenceOffset, ProducerSequenceFieldSize),
            producerSequence);
        BinaryPrimitives.WriteInt64BigEndian(
            frame.Slice(SequenceIdOffset, SequenceIdFieldSize),
            sequenceId);

        int checksumOffset = frameLength - ChecksumFieldSize;
        uint checksum = Crc32C.Compute(frame[..checksumOffset]);
        BinaryPrimitives.WriteUInt32BigEndian(
            frame.Slice(checksumOffset, ChecksumFieldSize),
            checksum);
        return frameLength;
    }

    /// <summary>
    /// Allocates and encodes one frame.
    /// </summary>
    public static CanonicalFrame Encode(
        MessageType messageType,
        Guid sessionId,
        ushort producerId,
        ulong producerSequence,
        long sequenceId,
        ReadOnlySpan<byte> payload)
    {
        int frameLength = GetFrameLength(messageType, sessionId, payload.Length);
        byte[] bytes = new byte[frameLength];
        Encode(messageType, sessionId, producerId, producerSequence, sequenceId, payload, bytes);

        FrameHeader header = new(
            (uint)frameLength,
            CurrentVersion,
            messageType,
            sessionId,
            producerId,
            producerSequence,
            sequenceId);
        return new CanonicalFrame(
            bytes,
            header,
            bytes.AsMemory(HeaderSize, payload.Length));
    }

    /// <summary>
    /// Reads and validates the frame length prefix.
    /// </summary>
    public static int ReadFrameLength(ReadOnlySpan<byte> source)
    {
        if (source.Length < FrameLengthFieldSize)
        {
            throw new InvalidDataException("The frame length prefix is incomplete.");
        }

        uint declaredFrameLength = BinaryPrimitives.ReadUInt32BigEndian(source[..FrameLengthFieldSize]);
        if (declaredFrameLength is < MinimumFrameSize or > MaximumFrameSize)
        {
            throw new InvalidDataException(
                $"Frame length must be between {MinimumFrameSize} and {MaximumFrameSize} bytes.");
        }

        return (int)declaredFrameLength;
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

        int frameLength;
        try
        {
            frameLength = ReadFrameLength(source);
        }
        catch (InvalidDataException)
        {
            return OperationStatus.InvalidData;
        }

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
        var messageType = (MessageType)encodedMessageType;
        if (!Enum.IsDefined(messageType))
        {
            return OperationStatus.InvalidData;
        }

        int checksumOffset = frameLength - ChecksumFieldSize;
        uint encodedChecksum = BinaryPrimitives.ReadUInt32BigEndian(
            source.Slice(checksumOffset, ChecksumFieldSize));
        uint computedChecksum = Crc32C.Compute(source[..checksumOffset]);
        if (encodedChecksum != computedChecksum)
        {
            return OperationStatus.InvalidData;
        }

        Guid sessionId = new(
            source.Slice(SessionIdOffset, SessionIdFieldSize),
            bigEndian: true);
        if (sessionId == Guid.Empty)
        {
            return OperationStatus.InvalidData;
        }

        header = new FrameHeader(
            (uint)frameLength,
            CurrentVersion,
            messageType,
            sessionId,
            BinaryPrimitives.ReadUInt16BigEndian(source.Slice(ProducerIdOffset, ProducerIdFieldSize)),
            BinaryPrimitives.ReadUInt64BigEndian(
                source.Slice(ProducerSequenceOffset, ProducerSequenceFieldSize)),
            BinaryPrimitives.ReadInt64BigEndian(source.Slice(SequenceIdOffset, SequenceIdFieldSize)));
        int payloadLength = frameLength - MinimumFrameSize;
        payload = source.Slice(HeaderSize, payloadLength);
        return OperationStatus.Done;
    }

    public static CanonicalFrame DecodeSubmission(ReadOnlyMemory<byte> source)
    {
        CanonicalFrame frame = Decode(source);
        FrameHeader header = frame.Header;
        if (header.MessageType == MessageType.CommitThrough
            || header.ProducerId == ControlProducerId
            || header.ProducerSequence == 0
            || header.SequenceId != 0)
        {
            throw new InvalidDataException("Frame is not a valid submission.");
        }

        return frame;
    }

    public static CanonicalFrame DecodeSequencedCandidate(ReadOnlyMemory<byte> source)
    {
        CanonicalFrame frame = Decode(source);
        FrameHeader header = frame.Header;
        if (header.MessageType == MessageType.CommitThrough
            || header.ProducerId == ControlProducerId
            || header.ProducerSequence == 0
            || header.SequenceId <= 0)
        {
            throw new InvalidDataException("Frame is not a valid sequenced candidate.");
        }

        return frame;
    }

    internal static CanonicalFrame Decode(ReadOnlyMemory<byte> source)
    {
        OperationStatus status = TryDecode(source.Span, out FrameHeader header, out _);
        if (status != OperationStatus.Done)
        {
            throw new InvalidDataException("Source does not contain one valid canonical frame.");
        }

        return new CanonicalFrame(
            source,
            header,
            source.Slice(HeaderSize, source.Length - MinimumFrameSize));
    }

    private static int GetFrameLength(
        MessageType messageType,
        Guid sessionId,
        int payloadLength)
    {
        if (!Enum.IsDefined(messageType))
        {
            throw new ArgumentOutOfRangeException(nameof(messageType));
        }

        if (sessionId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionId));
        }

        if (payloadLength > MaximumFrameSize - MinimumFrameSize)
        {
            throw new ArgumentOutOfRangeException("payload");
        }

        return MinimumFrameSize + payloadLength;
    }
}
