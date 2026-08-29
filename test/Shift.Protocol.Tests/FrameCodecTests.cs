using System.Buffers;
using System.Buffers.Binary;
using Shift.Protocol.Framing;
using Xunit;

namespace Shift.Protocol.Tests;

public class FrameCodecTests
{
    private static readonly Guid _messageId = new("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly byte[] _payload = [0xde, 0xad, 0xbe, 0xef];
    private static readonly byte[] _encodedFrame =
    [
        0x00, 0x00, 0x00, 0x27,
        0x01,
        0x00, 0x04,
        0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77,
        0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff,
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        0xde, 0xad, 0xbe, 0xef,
        0x06, 0x71, 0xce, 0xdd,
    ];

    [Fact]
    public void EncodeWritesCanonicalBytes()
    {
        byte[] destination = new byte[_encodedFrame.Length];

        int bytesWritten = FrameCodec.Encode(
            MessageType.PlaceOrder,
            _messageId,
            0x0102030405060708,
            _payload,
            destination);

        Assert.Equal(_encodedFrame.Length, bytesWritten);
        Assert.Equal(_encodedFrame, destination);
    }

    [Fact]
    public void TryDecodeReadsCanonicalFrame()
    {
        OperationStatus status = FrameCodec.TryDecode(
            _encodedFrame,
            out FrameHeader header,
            out ReadOnlySpan<byte> payload,
            out int bytesConsumed);

        Assert.Equal(OperationStatus.Done, status);
        Assert.Equal((uint)_encodedFrame.Length, header.TotalLength);
        Assert.Equal(FrameCodec.CurrentVersion, header.Version);
        Assert.Equal(MessageType.PlaceOrder, header.MessageType);
        Assert.Equal(_messageId, header.MessageId);
        Assert.Equal(0x0102030405060708, header.SequenceId);
        Assert.Equal(_payload, payload.ToArray());
        Assert.Equal(_encodedFrame.Length, bytesConsumed);
    }

    [Fact]
    public void DecodeEncodePreservesCanonicalBytes()
    {
        Assert.Equal(
            OperationStatus.Done,
            FrameCodec.TryDecode(
                _encodedFrame,
                out FrameHeader header,
                out ReadOnlySpan<byte> payload,
                out _));
        byte[] destination = new byte[_encodedFrame.Length];

        FrameCodec.Encode(header.MessageType, header.MessageId, header.SequenceId, payload, destination);

        Assert.Equal(_encodedFrame, destination);
    }

    [Fact]
    public void TryDecodeReportsEveryTruncatedPrefix()
    {
        for (int length = 0; length < _encodedFrame.Length; length++)
        {
            OperationStatus status = FrameCodec.TryDecode(
                _encodedFrame.AsSpan(0, length),
                out _,
                out _,
                out int bytesConsumed);

            Assert.Equal(OperationStatus.NeedMoreData, status);
            Assert.Equal(0, bytesConsumed);
        }
    }

    [Fact]
    public void TryDecodeConsumesOneFrame()
    {
        byte[] source = new byte[_encodedFrame.Length * 2];
        _encodedFrame.CopyTo(source, 0);
        _encodedFrame.CopyTo(source, _encodedFrame.Length);

        OperationStatus status = FrameCodec.TryDecode(
            source,
            out _,
            out ReadOnlySpan<byte> payload,
            out int bytesConsumed);

        Assert.Equal(OperationStatus.Done, status);
        Assert.Equal(_payload, payload.ToArray());
        Assert.Equal(_encodedFrame.Length, bytesConsumed);
    }

    [Fact]
    public void TryDecodeRejectsLengthBelowMinimum()
    {
        byte[] frame = new byte[FrameCodec.MinimumFrameSize];
        BinaryPrimitives.WriteUInt32BigEndian(frame, FrameCodec.MinimumFrameSize - 1);

        OperationStatus status = FrameCodec.TryDecode(frame, out _, out _, out int bytesConsumed);

        Assert.Equal(OperationStatus.InvalidData, status);
        Assert.Equal(0, bytesConsumed);
    }

    [Fact]
    public void TryDecodeRejectsLengthAboveSupportedRange()
    {
        byte[] frame = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(frame, uint.MaxValue);

        OperationStatus status = FrameCodec.TryDecode(frame, out _, out _, out int bytesConsumed);

        Assert.Equal(OperationStatus.InvalidData, status);
        Assert.Equal(0, bytesConsumed);
    }

    [Fact]
    public void TryDecodeRejectsUnsupportedVersion()
    {
        byte[] frame = _encodedFrame.ToArray();
        frame[4]++;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(^FrameCodec.ChecksumSize), 0x8f986f4a);

        OperationStatus status = FrameCodec.TryDecode(frame, out _, out _, out int bytesConsumed);

        Assert.Equal(OperationStatus.InvalidData, status);
        Assert.Equal(0, bytesConsumed);
    }

    [Fact]
    public void TryDecodeRejectsUnsupportedVersionBeforeBodyArrives()
    {
        byte[] frame = _encodedFrame.AsSpan(0, 5).ToArray();
        frame[4]++;

        OperationStatus status = FrameCodec.TryDecode(frame, out _, out _, out int bytesConsumed);

        Assert.Equal(OperationStatus.InvalidData, status);
        Assert.Equal(0, bytesConsumed);
    }

    [Fact]
    public void CodecRejectsMessageTypeZero()
    {
        byte[] destination = new byte[FrameCodec.MinimumFrameSize];
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FrameCodec.Encode(default, _messageId, 1, [], destination));

        byte[] frame = _encodedFrame.AsSpan(0, 7).ToArray();
        frame[5] = 0;
        frame[6] = 0;

        OperationStatus status = FrameCodec.TryDecode(frame, out _, out _, out int bytesConsumed);

        Assert.Equal(OperationStatus.InvalidData, status);
        Assert.Equal(0, bytesConsumed);
    }

    [Fact]
    public void TryDecodeRejectsCorruptPayload()
    {
        byte[] frame = _encodedFrame.ToArray();
        frame[FrameCodec.HeaderSize] ^= 0xff;

        OperationStatus status = FrameCodec.TryDecode(frame, out _, out _, out int bytesConsumed);

        Assert.Equal(OperationStatus.InvalidData, status);
        Assert.Equal(0, bytesConsumed);
    }

    [Fact]
    public void EncodeRejectsDestinationTooSmall()
    {
        byte[] destination = new byte[_encodedFrame.Length - 1];

        Assert.Throws<ArgumentException>(() =>
            FrameCodec.Encode(MessageType.PlaceOrder, _messageId, 1, _payload, destination));
    }

    [Fact]
    public void EncodeSupportsOverlappingPayloadAndDestination()
    {
        byte[] destination = new byte[_encodedFrame.Length];
        _payload.CopyTo(destination, 0);

        FrameCodec.Encode(
            MessageType.PlaceOrder,
            _messageId,
            0x0102030405060708,
            destination.AsSpan(0, _payload.Length),
            destination);

        Assert.Equal(_encodedFrame, destination);
    }
}
