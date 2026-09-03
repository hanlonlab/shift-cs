using Shift.Protocol.Framing;
using Xunit;

namespace Shift.Sequencer.Tests;

public class VerifiedSubmissionTests
{
    private static readonly Guid _sessionId = new("10213243-5465-7687-98a9-bacbdcedfe0f");

    [Fact]
    public void AcceptsSubmissionDecodedByFrameCodec()
    {
        CanonicalFrame frame = FrameCodec.Encode(
            MessageType.StartNewSession,
            _sessionId,
            1,
            1,
            0,
            []);

        VerifiedSubmission.Verify(frame.Bytes);
    }

    [Fact]
    public void RejectsFramesOutsideTheSubmissionRole()
    {
        CanonicalFrame sequenced = FrameCodec.Encode(
            MessageType.StartNewSession,
            _sessionId,
            1,
            1,
            1,
            []);
        CanonicalFrame commit = FrameCodec.Encode(
            MessageType.CommitThrough,
            _sessionId,
            1,
            1,
            0,
            []);
        CanonicalFrame controlProducer = FrameCodec.Encode(
            MessageType.StartNewSession,
            _sessionId,
            FrameCodec.ControlProducerId,
            1,
            0,
            []);
        CanonicalFrame zeroProducerSequence = FrameCodec.Encode(
            MessageType.StartNewSession,
            _sessionId,
            1,
            0,
            0,
            []);

        Assert.Throws<InvalidDataException>(() => VerifiedSubmission.Verify(sequenced.Bytes));
        Assert.Throws<InvalidDataException>(() => VerifiedSubmission.Verify(commit.Bytes));
        Assert.Throws<InvalidDataException>(() => VerifiedSubmission.Verify(controlProducer.Bytes));
        Assert.Throws<InvalidDataException>(() => VerifiedSubmission.Verify(zeroProducerSequence.Bytes));
    }

    [Fact]
    public void RejectsMalformedFrame()
    {
        byte[] corrupt = FrameCodec.Encode(
            MessageType.StartNewSession,
            _sessionId,
            1,
            1,
            0,
            []).Bytes.ToArray();
        corrupt[FrameCodec.HeaderSize] ^= 0xff;

        Assert.Throws<InvalidDataException>(() => VerifiedSubmission.Verify(corrupt));
    }
}
