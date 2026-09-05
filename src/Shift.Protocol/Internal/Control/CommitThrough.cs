using Shift.Protocol.Framing;

namespace Shift.Protocol.Internal.Control;

public static class CommitThroughCodec
{
    public static CanonicalFrame Encode(Guid sessionId, long sequenceId)
    {
        if (sequenceId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequenceId));
        }

        return FrameCodec.Encode(
            MessageType.CommitThrough,
            sessionId,
            FrameCodec.ControlProducerId,
            0,
            sequenceId,
            []);
    }

    public static CanonicalFrame Decode(ReadOnlyMemory<byte> source)
    {
        CanonicalFrame frame = FrameCodec.Decode(source);
        Validate(frame);
        return frame;
    }

    /// <summary>Validates the commit-through role of an already canonical frame.</summary>
    public static void Validate(CanonicalFrame frame)
    {
        FrameHeader header = frame.Header;
        if (frame.Bytes.IsEmpty
            || header.MessageType != MessageType.CommitThrough
            || header.ProducerId != FrameCodec.ControlProducerId
            || header.ProducerSequence != 0
            || header.SequenceId <= 0
            || !frame.Payload.IsEmpty)
        {
            throw new InvalidDataException("Frame is not a valid commit-through acknowledgement.");
        }
    }
}
