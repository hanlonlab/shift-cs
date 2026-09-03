using System.Buffers;
using System.Buffers.Binary;
using Shift.Protocol.Framing;
using Xunit;

namespace Shift.Protocol.Tests;

public class FrameCodecTests
{
    private const int FrameLengthFieldSize = sizeof(uint);
    private const int VersionOffset = FrameLengthFieldSize;
    private const int VersionFieldSize = sizeof(byte);
    private const int MessageTypeOffset = VersionOffset + VersionFieldSize;
    private const int MessageTypeFieldSize = sizeof(ushort);

    private const ushort ProducerId = 0x0011;
    private const ulong ProducerSequence = 0x2233445566778899;
    private static readonly byte[] _payload = [0xde, 0xad, 0xbe, 0xef];
    private static readonly byte[] _encodedFrame =
    [
        0x00, 0x00, 0x00, 0x21,
        0x01,
        0x00, 0x04,
        0x00, 0x11,
        0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99,
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        0xde, 0xad, 0xbe, 0xef,
        0x40, 0xa4, 0x5e, 0x5a,
    ];

    [Fact]
    public void EncodeWritesCanonicalBytes()
    {
        byte[] destination = new byte[_encodedFrame.Length];

        int bytesWritten = FrameCodec.Encode(
            MessageType.PlaceOrder,
            ProducerId,
            ProducerSequence,
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
            out ReadOnlySpan<byte> payload);

        Assert.Equal(OperationStatus.Done, status);
        Assert.Equal((uint)_encodedFrame.Length, header.FrameLength);
        Assert.Equal(FrameCodec.CurrentVersion, header.Version);
        Assert.Equal(MessageType.PlaceOrder, header.MessageType);
        Assert.Equal(ProducerId, header.ProducerId);
        Assert.Equal(ProducerSequence, header.ProducerSequence);
        Assert.Equal(0x0102030405060708, header.SequenceId);
        Assert.Equal(_payload, payload.ToArray());
    }

    [Fact]
    public void DecodeEncodePreservesCanonicalBytes()
    {
        Assert.Equal(
            OperationStatus.Done,
            FrameCodec.TryDecode(
                _encodedFrame,
                out FrameHeader header,
                out ReadOnlySpan<byte> payload));
        byte[] destination = new byte[_encodedFrame.Length];

        FrameCodec.Encode(
            header.MessageType,
            header.ProducerId,
            header.ProducerSequence,
            header.SequenceId,
            payload,
            destination);

        Assert.Equal(_encodedFrame, destination);
    }

    [Fact]
    public void TryDecodeReportsEveryTruncatedFrame()
    {
        for (int length = 0; length < _encodedFrame.Length; length++)
        {
            OperationStatus status = FrameCodec.TryDecode(
                _encodedFrame.AsSpan(0, length),
                out _,
                out _);

            Assert.Equal(OperationStatus.NeedMoreData, status);
        }
    }

    [Fact]
    public void TryDecodeRejectsTrailingBytes()
    {
        byte[] source = new byte[_encodedFrame.Length + 1];
        _encodedFrame.CopyTo(source, 0);

        OperationStatus status = FrameCodec.TryDecode(
            source,
            out _,
            out _);

        Assert.Equal(OperationStatus.InvalidData, status);
    }

    [Fact]
    public void TryDecodeRejectsLengthBelowMinimum()
    {
        byte[] frame = new byte[FrameCodec.MinimumFrameSize];
        BinaryPrimitives.WriteUInt32BigEndian(
            frame.AsSpan(0, FrameLengthFieldSize),
            FrameCodec.MinimumFrameSize - 1);

        OperationStatus status = FrameCodec.TryDecode(frame, out _, out _);

        Assert.Equal(OperationStatus.InvalidData, status);
    }

    [Fact]
    public void TryDecodeRejectsLengthAboveSupportedRange()
    {
        byte[] frame = new byte[FrameLengthFieldSize];
        BinaryPrimitives.WriteUInt32BigEndian(
            frame.AsSpan(0, FrameLengthFieldSize),
            uint.MaxValue);

        OperationStatus status = FrameCodec.TryDecode(frame, out _, out _);

        Assert.Equal(OperationStatus.InvalidData, status);
    }

    [Fact]
    public void TryDecodeRejectsUnsupportedVersion()
    {
        byte[] frame = _encodedFrame.ToArray();
        frame[VersionOffset]++;
        int checksumOffset = frame.Length - FrameCodec.ChecksumFieldSize;
        BinaryPrimitives.WriteUInt32BigEndian(
            frame.AsSpan(checksumOffset, FrameCodec.ChecksumFieldSize),
            0x32f902fd);

        OperationStatus status = FrameCodec.TryDecode(frame, out _, out _);

        Assert.Equal(OperationStatus.InvalidData, status);
    }

    [Fact]
    public void CodecRejectsMessageTypeZero()
    {
        byte[] destination = new byte[FrameCodec.MinimumFrameSize];
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FrameCodec.Encode(default, ProducerId, ProducerSequence, 1, [], destination));

        byte[] frame = _encodedFrame.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(
            frame.AsSpan(MessageTypeOffset, MessageTypeFieldSize),
            0);
        int checksumOffset = frame.Length - FrameCodec.ChecksumFieldSize;
        BinaryPrimitives.WriteUInt32BigEndian(
            frame.AsSpan(checksumOffset, FrameCodec.ChecksumFieldSize),
            0x9207e84b);

        OperationStatus status = FrameCodec.TryDecode(frame, out _, out _);

        Assert.Equal(OperationStatus.InvalidData, status);
    }

    [Fact]
    public void TryDecodeRejectsCorruptPayload()
    {
        byte[] frame = _encodedFrame.ToArray();
        frame[FrameCodec.HeaderSize] ^= 0xff;

        OperationStatus status = FrameCodec.TryDecode(frame, out _, out _);

        Assert.Equal(OperationStatus.InvalidData, status);
    }

    [Fact]
    public void EncodeRejectsDestinationTooSmall()
    {
        byte[] destination = new byte[_encodedFrame.Length - 1];

        Assert.Throws<ArgumentException>(() =>
            FrameCodec.Encode(
                MessageType.PlaceOrder,
                ProducerId,
                ProducerSequence,
                1,
                _payload,
                destination));
    }

    [Fact]
    public void EncodeSupportsOverlappingPayloadAndDestination()
    {
        byte[] destination = new byte[_encodedFrame.Length];
        _payload.CopyTo(destination, 0);

        FrameCodec.Encode(
            MessageType.PlaceOrder,
            ProducerId,
            ProducerSequence,
            0x0102030405060708,
            destination.AsSpan(0, _payload.Length),
            destination);

        Assert.Equal(_encodedFrame, destination);
    }
}
