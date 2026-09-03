using Shift.Protocol.Framing;

namespace Shift.Protocol.Internal.Control;

public static class CommitThroughCodec
{
    public static CanonicalFrame Encode(long sequenceId)
    {
        if (sequenceId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequenceId));
        }

        return FrameCodec.Encode(
            MessageType.CommitThrough,
            FrameCodec.ControlProducerId,
            0,
            sequenceId,
            []);
    }

    public static CanonicalFrame Decode(ReadOnlyMemory<byte> source)
    {
        CanonicalFrame frame = FrameCodec.Decode(source);
        FrameHeader header = frame.Header;
        if (header.MessageType != MessageType.CommitThrough
            || header.ProducerId != FrameCodec.ControlProducerId
            || header.ProducerSequence != 0
            || header.SequenceId <= 0
            || !frame.Payload.IsEmpty)
        {
            throw new InvalidDataException("Frame is not a valid commit-through acknowledgement.");
        }

        return frame;
    }
}
