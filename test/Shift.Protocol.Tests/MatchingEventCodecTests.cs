using Shift.Protocol.Internal;
using Shift.Protocol.Internal.Events;
using Xunit;

namespace Shift.Protocol.Tests;

public class MatchingEventCodecTests
{
    [Fact]
    public void OrderUpdatedRoundTripsExpectedBytes()
    {
        var message = new OrderUpdated(
            0x0102030405060708,
            0x1112131415161718,
            0x2122232425262728,
            0x3132333435363738,
            RejectionReason.None,
            CancellationReason.ImmediateOrCancel);
        byte[] payload = new byte[34];

        int bytesWritten = OrderUpdatedCodec.Encode(message, payload);

        Assert.Equal(34, bytesWritten);
        Assert.Equal(
            new byte[]
            {
                0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
                0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
                0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28,
                0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38,
                0x00, 0x02
            },
            payload);
        Assert.True(OrderUpdatedCodec.TryDecode(payload, out OrderUpdated decoded));
        Assert.Equal(message, decoded);
    }

    [Fact]
    public void TradeExecutedRoundTripsExpectedBytes()
    {
        var message = new TradeExecuted(
            0x0102030405060708,
            new Fill(
                0x1112131415161718,
                0x2122232425262728,
                0x3132333435363738,
                FillRole.Maker));
        byte[] payload = new byte[33];

        int bytesWritten = TradeExecutedCodec.Encode(message, payload);

        Assert.Equal(33, bytesWritten);
        Assert.Equal(
            new byte[]
            {
                0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
                0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
                0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28,
                0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38,
                0x01
            },
            payload);
        Assert.True(TradeExecutedCodec.TryDecode(payload, out TradeExecuted decoded));
        Assert.Equal(message, decoded);
    }

    [Fact]
    public void EventCodecsRejectInvalidPayloads()
    {
        byte[] orderUpdated = new byte[34];
        orderUpdated[7] = 1;
        orderUpdated[15] = 1;
        orderUpdated[32] = byte.MaxValue;
        byte[] tradeExecuted = new byte[33];
        tradeExecuted[7] = 1;
        tradeExecuted[15] = 1;
        tradeExecuted[23] = 1;
        tradeExecuted[31] = 1;
        tradeExecuted[32] = byte.MaxValue;

        Assert.False(OrderUpdatedCodec.TryDecode(orderUpdated, out _));
        Assert.False(TradeExecutedCodec.TryDecode(tradeExecuted, out _));
        Assert.False(OrderUpdatedCodec.TryDecode(new byte[33], out _));
        Assert.False(TradeExecutedCodec.TryDecode(new byte[32], out _));
    }
}
