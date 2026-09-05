using Shift.Protocol.Framing;
using Shift.Protocol.Internal.Control;
using Xunit;

namespace Shift.Protocol.Tests;

public class CommitThroughCodecTests
{
    private static readonly Guid _sessionId = new("00112233-4455-6677-8899-aabbccddeeff");

    [Fact]
    public void EncodeProducesCanonicalControlFrame()
    {
        CanonicalFrame frame = CommitThroughCodec.Encode(_sessionId, 3);

        Assert.Equal(FrameCodec.MinimumFrameSize, frame.Bytes.Length);
        Assert.Equal(MessageType.CommitThrough, frame.Header.MessageType);
        Assert.Equal(_sessionId, frame.Header.SessionId);
        Assert.Equal(FrameCodec.ControlProducerId, frame.Header.ProducerId);
        Assert.Equal(0UL, frame.Header.ProducerSequence);
        Assert.Equal(3, frame.Header.SequenceId);
        Assert.True(frame.Payload.IsEmpty);
    }
}
