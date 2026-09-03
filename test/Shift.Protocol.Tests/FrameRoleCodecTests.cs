using Shift.Protocol.Framing;
using Xunit;

namespace Shift.Protocol.Tests;

public class FrameRoleCodecTests
{
    private const ushort ProducerId = 1;
    private const ulong ProducerSequence = 2;

    [Fact]
    public void DecodeSubmissionReturnsCanonicalFrame()
    {
        byte[] source = Encode(
            MessageType.PlaceOrder,
            ProducerId,
            ProducerSequence,
            0,
            [0xde, 0xad]);

        CanonicalFrame frame = FrameCodec.DecodeSubmission(source);

        Assert.Equal(source, frame.Bytes.ToArray());
        Assert.Equal(MessageType.PlaceOrder, frame.Header.MessageType);
        Assert.Equal(ProducerId, frame.Header.ProducerId);
        Assert.Equal(ProducerSequence, frame.Header.ProducerSequence);
        Assert.Equal(0, frame.Header.SequenceId);
        Assert.Equal(new byte[] { 0xde, 0xad }, frame.Payload.ToArray());
    }

    [Fact]
    public void DecodeSubmissionRejectsEveryIncorrectRoleField()
    {
        byte[][] invalidFrames =
        [
            Encode(MessageType.CommitThrough, ProducerId, ProducerSequence, 0, []),
            Encode(MessageType.PlaceOrder, FrameCodec.ControlProducerId, ProducerSequence, 0, []),
            Encode(MessageType.PlaceOrder, ProducerId, 0, 0, []),
            Encode(MessageType.PlaceOrder, ProducerId, ProducerSequence, 1, []),
            Encode(MessageType.PlaceOrder, ProducerId, ProducerSequence, -1, []),
        ];

        foreach (byte[] frame in invalidFrames)
        {
            Assert.Throws<InvalidDataException>(() => FrameCodec.DecodeSubmission(frame));
        }
    }

    [Fact]
    public void DecodeSequencedCandidateReturnsCanonicalFrame()
    {
        byte[] source = Encode(
            MessageType.PlaceOrder,
            ProducerId,
            ProducerSequence,
            3,
            [0xde, 0xad]);

        CanonicalFrame frame = FrameCodec.DecodeSequencedCandidate(source);

        Assert.Equal(source, frame.Bytes.ToArray());
        Assert.Equal(MessageType.PlaceOrder, frame.Header.MessageType);
        Assert.Equal(3, frame.Header.SequenceId);
        Assert.Equal(new byte[] { 0xde, 0xad }, frame.Payload.ToArray());
    }

    [Fact]
    public void DecodeSequencedCandidateRejectsEveryIncorrectRoleField()
    {
        byte[][] invalidFrames =
        [
            Encode(MessageType.CommitThrough, ProducerId, ProducerSequence, 1, []),
            Encode(MessageType.PlaceOrder, FrameCodec.ControlProducerId, ProducerSequence, 1, []),
            Encode(MessageType.PlaceOrder, ProducerId, 0, 1, []),
            Encode(MessageType.PlaceOrder, ProducerId, ProducerSequence, 0, []),
            Encode(MessageType.PlaceOrder, ProducerId, ProducerSequence, -1, []),
        ];

        foreach (byte[] frame in invalidFrames)
        {
            Assert.Throws<InvalidDataException>(() => FrameCodec.DecodeSequencedCandidate(frame));
        }
    }

    [Fact]
    public void EncodeCommitThroughProducesCanonicalControlFrame()
    {
        CanonicalFrame frame = FrameCodec.EncodeCommitThrough(3);

        Assert.Equal(FrameCodec.MinimumFrameSize, frame.Bytes.Length);
        Assert.Equal(MessageType.CommitThrough, frame.Header.MessageType);
        Assert.Equal(FrameCodec.ControlProducerId, frame.Header.ProducerId);
        Assert.Equal(0UL, frame.Header.ProducerSequence);
        Assert.Equal(3, frame.Header.SequenceId);
        Assert.True(frame.Payload.IsEmpty);
        Assert.Equal(frame.Bytes.ToArray(), FrameCodec.DecodeCommitThrough(frame.Bytes).Bytes.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void EncodeCommitThroughRejectsNonpositiveSequence(long sequenceId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FrameCodec.EncodeCommitThrough(sequenceId));
    }

    [Fact]
    public void DecodeCommitThroughRejectsEveryIncorrectRoleField()
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
            Assert.Throws<InvalidDataException>(() => FrameCodec.DecodeCommitThrough(frame));
        }
    }

    [Fact]
    public void RoleDecodersRejectMalformedFrames()
    {
        byte[] submission = Encode(
            MessageType.PlaceOrder,
            ProducerId,
            ProducerSequence,
            0,
            []);
        submission[^1] ^= 0xff;

        byte[] candidate = Encode(
            MessageType.PlaceOrder,
            ProducerId,
            ProducerSequence,
            1,
            []);
        candidate[^1] ^= 0xff;

        byte[] commit = FrameCodec.EncodeCommitThrough(1).Bytes.ToArray();
        commit[^1] ^= 0xff;

        Assert.Throws<InvalidDataException>(() => FrameCodec.DecodeSubmission(submission));
        Assert.Throws<InvalidDataException>(() => FrameCodec.DecodeSequencedCandidate(candidate));
        Assert.Throws<InvalidDataException>(() => FrameCodec.DecodeCommitThrough(commit));
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
