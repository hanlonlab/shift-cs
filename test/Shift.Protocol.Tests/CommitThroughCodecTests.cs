using Shift.Protocol.Framing;
using Shift.Protocol.Internal.Control;
using Xunit;

namespace Shift.Protocol.Tests;

public class CommitThroughCodecTests
{
    private const ushort ProducerId = 1;
    private const ulong ProducerSequence = 2;

    [Fact]
    public void EncodeProducesCanonicalControlFrame()
    {
        CanonicalFrame frame = CommitThroughCodec.Encode(3);

        Assert.Equal(FrameCodec.MinimumFrameSize, frame.Bytes.Length);
        Assert.Equal(MessageType.CommitThrough, frame.Header.MessageType);
        Assert.Equal(FrameCodec.ControlProducerId, frame.Header.ProducerId);
        Assert.Equal(0UL, frame.Header.ProducerSequence);
        Assert.Equal(3, frame.Header.SequenceId);
        Assert.True(frame.Payload.IsEmpty);
        Assert.Equal(frame.Bytes.ToArray(), CommitThroughCodec.Decode(frame.Bytes).Bytes.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void EncodeRejectsNonpositiveSequence(long sequenceId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CommitThroughCodec.Encode(sequenceId));
    }

    [Fact]
    public void DecodeRejectsEveryIncorrectRoleField()
    {
        byte[][] invalidFrames =
        [
            Encode(MessageType.PlaceOrder, FrameCodec.ControlProducerId, 0, 1, []),
            Encode(MessageType.CommitThrough, ProducerId, 0, 1, []),
            Encode(MessageType.CommitThrough, FrameCodec.ControlProducerId, ProducerSequence, 1, []),
            Encode(MessageType.CommitThrough, FrameCodec.ControlProducerId, 0, 0, []),
            Encode(MessageType.CommitThrough, FrameCodec.ControlProducerId, 0, -1, []),
            Encode(MessageType.CommitThrough, FrameCodec.ControlProducerId, 0, 1, [0x01]),
        ];

        foreach (byte[] frame in invalidFrames)
        {
            Assert.Throws<InvalidDataException>(() => CommitThroughCodec.Decode(frame));
        }
    }

    [Fact]
    public void DecodeRejectsMalformedFrame()
    {
        byte[] frame = CommitThroughCodec.Encode(1).Bytes.ToArray();
        frame[^1] ^= 0xff;

        Assert.Throws<InvalidDataException>(() => CommitThroughCodec.Decode(frame));
    }

    private static byte[] Encode(
        MessageType messageType,
        ushort producerId,
        ulong producerSequence,
        long sequenceId,
        ReadOnlySpan<byte> payload) =>
        FrameCodec.Encode(
            messageType,
            producerId,
            producerSequence,
            sequenceId,
            payload).Bytes.ToArray();
}
