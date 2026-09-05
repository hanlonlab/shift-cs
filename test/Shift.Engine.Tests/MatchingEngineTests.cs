using Shift.Engine.Matching;
using Shift.Protocol.Internal;
using Shift.Protocol.Internal.Commands;
using Xunit;

namespace Shift.Engine.Tests;

public sealed class MatchingEngineTests
{
    private const long PairId = 7;

    [Fact]
    public void RepeatedStartPreservesExistingOrders()
    {
        MatchingEngine engine = CreateActiveEngine();
        engine.Place(new PlaceOrder(PairId, 1, OrderSide.Buy, 100, 10, OrderType.DayLimit));

        Assert.Equal(StartSessionStatus.AlreadyStarted, engine.StartSession(new StartNewSession()));

        Assert.True(engine.IsSessionActive);
        AssertSingleBidUnchanged(engine);
    }

    [Theory]
    [InlineData(8, 2, OrderSide.Buy, 100, 10, OrderType.DayLimit, RejectionReason.InvalidPairId)]
    [InlineData(PairId, 0, OrderSide.Buy, 100, 10, OrderType.DayLimit, RejectionReason.InvalidOrderId)]
    [InlineData(PairId, 2, (OrderSide)255, 100, 10, OrderType.DayLimit, RejectionReason.InvalidOrderSide)]
    [InlineData(PairId, 2, OrderSide.Buy, 0, 10, OrderType.DayLimit, RejectionReason.InvalidPrice)]
    [InlineData(PairId, 2, OrderSide.Buy, 100, 0, OrderType.DayLimit, RejectionReason.InvalidQuantity)]
    [InlineData(PairId, 2, OrderSide.Buy, 100, 10, (OrderType)255, RejectionReason.UnsupportedOrderType)]
    [InlineData(PairId, 2, OrderSide.Buy, 100, 10, OrderType.PostOnlyLimit, RejectionReason.UnsupportedOrderType)]
    [InlineData(PairId, 1, OrderSide.Sell, 99, 20, OrderType.DayLimit, RejectionReason.DuplicateOrderId)]
    public void InvalidPlacementLeavesExistingOrdersUnchanged(
        long pairId, long orderId, OrderSide side, long priceTicks, long quantity,
        OrderType orderType, RejectionReason expectedReason)
    {
        MatchingEngine engine = CreateActiveEngine();
        engine.Place(new PlaceOrder(PairId, 1, OrderSide.Buy, 100, 10, OrderType.DayLimit));

        OrderResult result = engine.Place(new PlaceOrder(pairId, orderId, side, priceTicks, quantity, orderType));

        Assert.Equal(new OrderResult(expectedReason), result);
        AssertSingleBidUnchanged(engine);
    }

    [Theory]
    [InlineData(OrderSide.Buy, OrderSide.Sell, 99)]
    [InlineData(OrderSide.Buy, OrderSide.Sell, 100)]
    public void CrossingOwnOrderCancelsOnlyTheNewOrder(
        OrderSide restingSide, OrderSide incomingSide, long incomingPrice)
    {
        MatchingEngine engine = CreateActiveEngine();
        engine.Place(new PlaceOrder(PairId, 1, restingSide, 100, 10, OrderType.DayLimit));

        OrderResult result = engine.Place(new PlaceOrder(PairId, 2, incomingSide, incomingPrice, 20, OrderType.DayLimit));

        Assert.Equal(new OrderResult(RejectionReason.None, 0, 20, CancellationReason.SelfMatchPrevention), result);
        Assert.False(engine.TryGetOrder(2, out _));
        Assert.Equal(1, engine.LiveOrderCount);
        Assert.True(engine.TryGetOrder(1, out RestingOrder resting));
        Assert.Equal(new RestingOrder(1, restingSide, 100, 10), resting);
    }

    [Fact]
    public void ReductionPreservesPriorityAndFullReductionAllowsIdReuseAtTheTail()
    {
        MatchingEngine engine = CreateActiveEngine();
        engine.Place(new PlaceOrder(PairId, 1, OrderSide.Sell, 100, 10, OrderType.DayLimit));
        engine.Place(new PlaceOrder(PairId, 2, OrderSide.Sell, 100, 20, OrderType.DayLimit));
        engine.Place(new PlaceOrder(PairId, 3, OrderSide.Sell, 100, 30, OrderType.DayLimit));

        Assert.Equal(new OrderResult(RejectionReason.None, 6, 4, CancellationReason.Requested), engine.Reduce(PairId, 1, 4));
        Assert.True(engine.TryGetOrder(1, out RestingOrder reduced));
        Assert.Equal(6, reduced.RemainingQuantity);
        Assert.Equal(new OrderResult(RejectionReason.None, 0, 20, CancellationReason.Requested), engine.Reduce(PairId, 2, 20));
        Assert.False(engine.TryGetOrder(2, out _));
        Assert.Equal(2, engine.LiveOrderCount);
        engine.Place(new PlaceOrder(PairId, 2, OrderSide.Sell, 100, 5, OrderType.DayLimit));

        var cancellations = new CanceledOrder[3];
        Assert.Equal(RejectionReason.None, engine.EndSession(new EndCurrentSession(), cancellations, out int count));
        Assert.Equal(3, count);
        Assert.Equal(
            new[] { new CanceledOrder(1, 6), new CanceledOrder(3, 30), new CanceledOrder(2, 5) },
            cancellations);
    }

    [Theory]
    [InlineData(PairId, 2, 1, RejectionReason.UnknownOrder)]
    [InlineData(PairId, 1, 0, RejectionReason.InvalidQuantity)]
    [InlineData(PairId, 1, 11, RejectionReason.ReductionExceedsRemainingQuantity)]
    public void InvalidReductionLeavesTheOrderUnchanged(
        long pairId, long orderId, long quantity, RejectionReason expectedReason)
    {
        MatchingEngine engine = CreateActiveEngine();
        engine.Place(new PlaceOrder(PairId, 1, OrderSide.Buy, 100, 10, OrderType.DayLimit));

        Assert.Equal(new OrderResult(expectedReason), engine.Reduce(pairId, orderId, quantity));
        AssertSingleBidUnchanged(engine);
    }

    [Fact]
    public void EndSessionCancelsInPriceThenFifoOrderAndClearsTheBook()
    {
        MatchingEngine engine = CreateActiveEngine();
        engine.Place(new PlaceOrder(PairId, 1, OrderSide.Sell, 103, 10, OrderType.DayLimit));
        engine.Place(new PlaceOrder(PairId, 2, OrderSide.Buy, 99, 20, OrderType.DayLimit));
        engine.Place(new PlaceOrder(PairId, 3, OrderSide.Buy, 100, 30, OrderType.DayLimit));
        engine.Place(new PlaceOrder(PairId, 4, OrderSide.Sell, 102, 40, OrderType.DayLimit));
        engine.Place(new PlaceOrder(PairId, 5, OrderSide.Buy, 100, 50, OrderType.DayLimit));
        engine.Place(new PlaceOrder(PairId, 6, OrderSide.Sell, 102, 60, OrderType.DayLimit));
        engine.Reduce(PairId, 3, 7);
        var sentinel = new CanceledOrder(999, 999);
        var cancellations = new CanceledOrder[7];
        cancellations[6] = sentinel;

        RejectionReason result = engine.EndSession(new EndCurrentSession(), cancellations, out int count);

        Assert.Equal(RejectionReason.None, result);
        Assert.Equal(6, count);
        Assert.Equal(
            new[]
            {
                new CanceledOrder(3, 23), new CanceledOrder(5, 50), new CanceledOrder(2, 20),
                new CanceledOrder(4, 40), new CanceledOrder(6, 60), new CanceledOrder(1, 10), sentinel
            },
            cancellations);
        Assert.False(engine.IsSessionActive);
        Assert.Equal(0, engine.LiveOrderCount);
        for (long orderId = 1; orderId <= 6; orderId++)
        {
            Assert.False(engine.TryGetOrder(orderId, out _));
        }
    }

    [Fact]
    public void UndersizedEndOutputChangesNothingAndCanBeRetried()
    {
        MatchingEngine engine = CreateActiveEngine();
        engine.Place(new PlaceOrder(PairId, 1, OrderSide.Buy, 100, 10, OrderType.DayLimit));
        engine.Place(new PlaceOrder(PairId, 2, OrderSide.Sell, 101, 20, OrderType.DayLimit));
        var sentinel = new CanceledOrder(999, 999);
        CanceledOrder[] tooSmall = [sentinel];

        Assert.Equal(RejectionReason.InvalidOutputBuffer, engine.EndSession(new EndCurrentSession(), tooSmall, out int count));

        Assert.Equal(0, count);
        Assert.Equal(sentinel, tooSmall[0]);
        Assert.True(engine.IsSessionActive);
        Assert.Equal(2, engine.LiveOrderCount);
        Assert.True(engine.TryGetOrder(1, out RestingOrder bid));
        Assert.Equal(new RestingOrder(1, OrderSide.Buy, 100, 10), bid);
        Assert.True(engine.TryGetOrder(2, out RestingOrder ask));
        Assert.Equal(new RestingOrder(2, OrderSide.Sell, 101, 20), ask);
        var sufficient = new CanceledOrder[2];
        Assert.Equal(RejectionReason.None, engine.EndSession(new EndCurrentSession(), sufficient, out count));
        Assert.Equal(2, count);
        Assert.Equal(new[] { new CanceledOrder(1, 10), new CanceledOrder(2, 20) }, sufficient);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrderInstructionsAndEndFailOutsideAnActiveSession(bool sessionEnded)
    {
        MatchingEngine engine = new(PairId);
        if (sessionEnded)
        {
            engine.StartSession(new StartNewSession());
            Assert.Equal(RejectionReason.None, engine.EndSession(new EndCurrentSession(), [], out int count));
            Assert.Equal(0, count);
        }

        var expected = new OrderResult(RejectionReason.DayNotStarted);
        Assert.Equal(expected, engine.Place(new PlaceOrder(PairId, 1, OrderSide.Buy, 100, 10, OrderType.DayLimit)));
        Assert.Equal(expected, engine.Cancel(new CancelOrder(PairId, 1)));
        Assert.Equal(expected, engine.Reduce(PairId, 1, 1));
        var sentinel = new CanceledOrder(999, 999);
        CanceledOrder[] output = [sentinel];
        Assert.Equal(RejectionReason.DayNotStarted, engine.EndSession(new EndCurrentSession(), output, out int written));
        Assert.Equal(0, written);
        Assert.Equal(sentinel, output[0]);
        Assert.Equal(0, engine.LiveOrderCount);
        Assert.False(engine.IsSessionActive);
        Assert.Equal(
            sessionEnded ? StartSessionStatus.SessionEnded : StartSessionStatus.Started,
            engine.StartSession(new StartNewSession()));
    }

    private static MatchingEngine CreateActiveEngine()
    {
        MatchingEngine engine = new(PairId);
        Assert.Equal(StartSessionStatus.Started, engine.StartSession(new StartNewSession()));
        return engine;
    }

    private static void AssertSingleBidUnchanged(MatchingEngine engine)
    {
        Assert.Equal(1, engine.LiveOrderCount);
        Assert.True(engine.TryGetOrder(1, out RestingOrder order));
        Assert.Equal(new RestingOrder(1, OrderSide.Buy, 100, 10), order);
    }
}
