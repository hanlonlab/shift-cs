using Shift.Protocol.Framing;
using Xunit;

namespace Shift.Protocol.Tests;

public class FrameRoleCodecTests
{
    private const ushort ProducerId = 1;
    private const ulong ProducerSequence = 2;
    private static readonly Guid _sessionId = new("00112233-4455-6677-8899-aabbccddeeff");

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
