using Shift.Protocol.Framing;
using Xunit;

namespace Shift.Protocol.Tests;

public class FrameRoleCodecTests
{
    private const ushort ProducerId = 1;
    private const ulong ProducerSequence = 2;
    private static readonly Guid _sessionId = new("00112233-4455-6677-8899-aabbccddeeff");

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
        Assert.Equal(_sessionId, frame.Header.SessionId);
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

        Assert.Throws<InvalidDataException>(() => FrameCodec.DecodeSubmission(submission));
        Assert.Throws<InvalidDataException>(() => FrameCodec.DecodeSequencedCandidate(candidate));
    }

    private static byte[] Encode(
        MessageType messageType,
        ushort producerId,
        ulong producerSequence,
        long sequenceId,
        ReadOnlySpan<byte> payload) =>
        FrameCodec.Encode(
            messageType,
            _sessionId,
            producerId,
            producerSequence,
            sequenceId,
            payload).Bytes.ToArray();
}
