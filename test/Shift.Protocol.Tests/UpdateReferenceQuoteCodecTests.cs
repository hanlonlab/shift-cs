using System.Buffers.Binary;
using Shift.Protocol.Internal;
using Shift.Protocol.Internal.Commands;
using Xunit;

namespace Shift.Protocol.Tests;

public class UpdateReferenceQuoteCodecTests
{
    [Fact]
    public void RoundTripsExpectedBytes()
    {
        var command = new UpdateReferenceQuote(
            0x0102030405060708,
            new ReferenceLevel(0x1112131415161718, 0x2122232425262728),
            new ReferenceLevel(0x3132333435363738, 0x4142434445464748));
        byte[] payload = new byte[40];

        Assert.Equal(40, UpdateReferenceQuoteCodec.Encode(command, payload));
        Assert.Equal(
            new byte[]
            {
                0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
                0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
                0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28,
                0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38,
                0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48
            },
            payload);
        Assert.True(UpdateReferenceQuoteCodec.TryDecode(payload, out UpdateReferenceQuote decoded));
        Assert.Equal(command, decoded);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(100, 5, 0, 0)]
    [InlineData(0, 0, 101, 5)]
    public void AcceptsAbsentSides(long bidPrice, long bidQuantity, long askPrice, long askQuantity)
    {
        var command = new UpdateReferenceQuote(
            1,
            new ReferenceLevel(bidPrice, bidQuantity),
            new ReferenceLevel(askPrice, askQuantity));
        byte[] payload = new byte[40];

        UpdateReferenceQuoteCodec.Encode(command, payload);

        Assert.True(UpdateReferenceQuoteCodec.TryDecode(payload, out UpdateReferenceQuote decoded));
        Assert.Equal(command, decoded);
    }

    [Theory]
    [InlineData(0, 100, 5, 101, 5)]
    [InlineData(-1, 100, 5, 101, 5)]
    [InlineData(1, 0, 5, 101, 5)]
    [InlineData(1, 100, 0, 101, 5)]
    [InlineData(1, -100, 5, 101, 5)]
    [InlineData(1, 100, -5, 101, 5)]
    [InlineData(1, 100, 5, 0, 5)]
    [InlineData(1, 100, 5, 101, 0)]
    [InlineData(1, 100, 5, -101, 5)]
    [InlineData(1, 100, 5, 101, -5)]
    [InlineData(1, 101, 5, 101, 5)]
    [InlineData(1, 102, 5, 101, 5)]
    public void RejectsInvalidQuotes(long pairId, long bidPrice, long bidQuantity, long askPrice, long askQuantity)
    {
        var command = new UpdateReferenceQuote(
            pairId,
            new ReferenceLevel(bidPrice, bidQuantity),
            new ReferenceLevel(askPrice, askQuantity));
        byte[] payload = new byte[40];
        BinaryPrimitives.WriteInt64BigEndian(payload, pairId);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(8), bidPrice);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(16), bidQuantity);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(24), askPrice);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(32), askQuantity);

        Assert.Throws<ArgumentOutOfRangeException>(() => UpdateReferenceQuoteCodec.Encode(command, payload));
        Assert.False(UpdateReferenceQuoteCodec.TryDecode(payload, out UpdateReferenceQuote decoded));
        Assert.Equal(default, decoded);
    }

    [Fact]
    public void RejectsTruncatedAndTrailingPayloads()
    {
        byte[] payload = new byte[41];
        var command = new UpdateReferenceQuote(1, new ReferenceLevel(100, 5), new ReferenceLevel(101, 5));
        UpdateReferenceQuoteCodec.Encode(command, payload);

        Assert.False(UpdateReferenceQuoteCodec.TryDecode(payload.AsSpan(0, 39), out _));
        Assert.False(UpdateReferenceQuoteCodec.TryDecode(payload, out _));
        Assert.Throws<ArgumentException>(() => UpdateReferenceQuoteCodec.Encode(command, new byte[39]));
    }
}
