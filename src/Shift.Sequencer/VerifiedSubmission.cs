using Shift.Protocol.Framing;

namespace Shift.Sequencer;

public readonly struct VerifiedSubmission
{
    private VerifiedSubmission(CanonicalFrame frame)
    {
        Frame = frame;
    }

    internal CanonicalFrame Frame { get; }

    public static VerifiedSubmission Verify(ReadOnlyMemory<byte> frame)
    {
        return new VerifiedSubmission(FrameCodec.DecodeSubmission(frame));
    }
}
