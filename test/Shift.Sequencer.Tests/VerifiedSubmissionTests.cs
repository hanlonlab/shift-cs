using Shift.Protocol.Framing;
using Shift.Protocol.Internal.Commands;
using Xunit;

namespace Shift.Sequencer.Tests;

public class VerifiedSubmissionTests
{
    private static readonly Guid _sessionId = new("10213243-5465-7687-98a9-bacbdcedfe0f");

    [Fact]
    public void AcceptsSubmissionDecodedByFrameCodec()
    {
        byte[] payload = new byte[16];
        StartNewSessionCodec.Encode(new StartNewSession(_sessionId), payload);
        CanonicalFrame frame = FrameCodec.Encode(MessageType.StartNewSession, 1, 1, 0, payload);

        VerifiedSubmission.Verify(frame.Bytes);
    }

    [Fact]
    public void RejectsFramesOutsideTheSubmissionRole()
    {
        byte[] payload = new byte[16];
        StartNewSessionCodec.Encode(new StartNewSession(_sessionId), payload);
        CanonicalFrame sequenced = FrameCodec.Encode(MessageType.StartNewSession, 1, 1, 1, payload);
        CanonicalFrame commit = FrameCodec.Encode(MessageType.CommitThrough, 1, 1, 0, []);
        CanonicalFrame controlProducer = FrameCodec.Encode(
            MessageType.StartNewSession,
            FrameCodec.ControlProducerId,
            1,
            0,
            payload);
        CanonicalFrame zeroProducerSequence = FrameCodec.Encode(
            MessageType.StartNewSession,
            1,
            0,
            0,
            payload);

        Assert.Throws<InvalidDataException>(() => VerifiedSubmission.Verify(sequenced.Bytes));
        Assert.Throws<InvalidDataException>(() => VerifiedSubmission.Verify(commit.Bytes));
        Assert.Throws<InvalidDataException>(() => VerifiedSubmission.Verify(controlProducer.Bytes));
        Assert.Throws<InvalidDataException>(() => VerifiedSubmission.Verify(zeroProducerSequence.Bytes));
    }

    [Fact]
    public void RejectsMalformedFrame()
    {
        byte[] payload = new byte[16];
        StartNewSessionCodec.Encode(new StartNewSession(_sessionId), payload);
        byte[] corrupt = FrameCodec.Encode(MessageType.StartNewSession, 1, 1, 0, payload).Bytes.ToArray();
        corrupt[FrameCodec.HeaderSize] ^= 0xff;

        Assert.Throws<InvalidDataException>(() => VerifiedSubmission.Verify(corrupt));
    }
}
