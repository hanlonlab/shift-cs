using Shift.Protocol.Internal;
using Shift.Protocol.Internal.Commands;
using Xunit;

namespace Shift.Protocol.Tests;

public class MatchingCommandCodecTests
{
    [Fact]
    public void PlaceOrderRoundTripsExpectedBytes()
    {
        var command = new PlaceOrder(
            0x0102030405060708,
            0x1112131415161718,
            OrderSide.Sell,
            0x2122232425262728,
            0x3132333435363738,
            OrderType.PostOnlyLimit);
        byte[] payload = new byte[34];

        int bytesWritten = PlaceOrderCodec.Encode(command, payload);

        Assert.Equal(34, bytesWritten);
        Assert.Equal(
            new byte[]
            {
                0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
                0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
                0x02,
                0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28,
                0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38,
                0x03
            },
            payload);
        Assert.True(PlaceOrderCodec.TryDecode(payload, out PlaceOrder decoded));
        Assert.Equal(command, decoded);
    }

    [Fact]
    public void PlaceOrderRejectsInvalidPayload()
    {
        byte[] invalidSide = new byte[34];
        invalidSide[7] = 1;
        invalidSide[15] = 1;
        invalidSide[24] = 1;
        invalidSide[32] = 1;
        invalidSide[33] = (byte)OrderType.DayLimit;

        Assert.False(PlaceOrderCodec.TryDecode(invalidSide, out PlaceOrder command));
        Assert.Equal(default, command);
        Assert.False(PlaceOrderCodec.TryDecode(new byte[33], out _));
    }

    [Fact]
    public void CancelOrderRoundTrips()
    {
        var command = new CancelOrder(
            0x0102030405060708,
            0x1112131415161718);
        byte[] payload = new byte[16];

        int bytesWritten = CancelOrderCodec.Encode(command, payload);

        Assert.Equal(16, bytesWritten);
        Assert.Equal(
            new byte[]
            {
                0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
                0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18
            },
            payload);
        Assert.True(CancelOrderCodec.TryDecode(payload, out CancelOrder decoded));
        Assert.Equal(command, decoded);
    }

    [Fact]
    public void CommandEncodersRejectInvalidValuesAndSmallDestinations()
    {
        var invalidPlace = new PlaceOrder(0, 1, OrderSide.Buy, 1, 1, OrderType.DayLimit);
        var validPlace = new PlaceOrder(1, 1, OrderSide.Buy, 1, 1, OrderType.DayLimit);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PlaceOrderCodec.Encode(invalidPlace, new byte[34]));
        Assert.Throws<ArgumentException>(() =>
            PlaceOrderCodec.Encode(validPlace, new byte[33]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CancelOrderCodec.Encode(new CancelOrder(0, 1), new byte[16]));
        Assert.Throws<ArgumentException>(() =>
            CancelOrderCodec.Encode(new CancelOrder(1, 1), new byte[15]));
    }
}
