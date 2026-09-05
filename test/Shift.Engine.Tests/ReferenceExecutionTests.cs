using Shift.Engine.Matching;
using Shift.Protocol.Internal;
using Shift.Protocol.Internal.Commands;
using Xunit;

namespace Shift.Engine.Tests;

public sealed class ReferenceExecutionTests
{
    private const long PairId = 1;

    [Theory]
    [InlineData(OrderSide.Buy, 102, 101)]
    [InlineData(OrderSide.Sell, 98, 99)]
    public void IncomingDayOrderUsesTheQuotePriceAndRestsItsRemainder(OrderSide side, long limit, long executionPrice)
    {
        MatchingEngine engine = CreateEngine();
        engine.UpdateReferenceQuote(PairId, new ReferenceLevel(99, 30), new ReferenceLevel(101, 30));
        var fills = new Fill[1];

        OrderResult result = engine.Place(new PlaceOrder(PairId, 1, side, limit, 50, OrderType.DayLimit), fills);

        Assert.Equal(new OrderResult(RejectionReason.None, RemainingQuantity: 20, FillCount: 1), result);
        Assert.Equal(new Fill(1, executionPrice, 30, FillRole.Taker), fills[0]);
        Assert.True(engine.TryGetOrder(1, out RestingOrder order));
        Assert.Equal(new RestingOrder(1, side, limit, 20), order);
        OrderSide opposite = side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        Assert.Equal(new ReferenceLevelState(executionPrice, 30, 0), engine.GetReferenceLevel(opposite));
    }

    [Theory]
    [InlineData(OrderType.DayLimit, 101, 0, 0)]
    [InlineData(OrderType.ImmediateOrCancelLimit, 101, 0, 0)]
    [InlineData(OrderType.ImmediateOrCancelLimit, 100, 0, 10)]
    public void FullyFilledOrdersAndIocRemaindersNeverRest(OrderType type, long limit, long remaining, long canceled)
    {
        MatchingEngine engine = CreateEngine();
        engine.UpdateReferenceQuote(PairId, default, new ReferenceLevel(101, 10));
        var fills = new Fill[1];

        OrderResult result = engine.Place(new PlaceOrder(PairId, 1, OrderSide.Buy, limit, 10, type), fills);

        Assert.Equal(RejectionReason.None, result.RejectionReason);
        Assert.Equal(remaining, result.RemainingQuantity);
        Assert.Equal(canceled, result.CanceledQuantity);
        Assert.Equal(canceled == 0 ? CancellationReason.None : CancellationReason.ImmediateOrCancel, result.CancellationReason);
        Assert.Equal(0, engine.LiveOrderCount);
        Assert.Equal(10, canceled + (result.FillCount == 0 ? 0 : fills[0].Quantity));
    }

    [Fact]
    public void PartialIocPreservesFillsAndCancelsTheRemainder()
    {
        MatchingEngine engine = CreateEngine();
        engine.UpdateReferenceQuote(PairId, default, new ReferenceLevel(101, 30));
        var fills = new Fill[1];

        OrderResult result = engine.Place(new PlaceOrder(PairId, 1, OrderSide.Buy, 102, 50, OrderType.ImmediateOrCancelLimit), fills);

        Assert.Equal(new OrderResult(RejectionReason.None, 0, 20, CancellationReason.ImmediateOrCancel, 1), result);
        Assert.Equal(new Fill(1, 101, 30, FillRole.Taker), fills[0]);
        Assert.Equal(0, engine.LiveOrderCount);
    }

    [Fact]
    public void RepeatedAndShrinkingQuotesAbsorbConsumptionBeforeRemovingMoreQuantity()
    {
        MatchingEngine engine = CreateEngine();
        engine.UpdateReferenceQuote(PairId, default, new ReferenceLevel(101, 100));
        var fills = new Fill[1];
        engine.Place(new PlaceOrder(PairId, 1, OrderSide.Buy, 101, 30, OrderType.DayLimit), fills);

        engine.UpdateReferenceQuote(PairId, default, new ReferenceLevel(101, 100));
        Assert.Equal(new ReferenceLevelState(101, 100, 70), engine.GetReferenceLevel(OrderSide.Sell));
        engine.UpdateReferenceQuote(PairId, default, new ReferenceLevel(101, 80));
        Assert.Equal(new ReferenceLevelState(101, 80, 70), engine.GetReferenceLevel(OrderSide.Sell));
        engine.UpdateReferenceQuote(PairId, default, new ReferenceLevel(101, 60));
        Assert.Equal(new ReferenceLevelState(101, 60, 60), engine.GetReferenceLevel(OrderSide.Sell));
        engine.UpdateReferenceQuote(PairId, default, new ReferenceLevel(101, 65));
        Assert.Equal(new ReferenceLevelState(101, 65, 65), engine.GetReferenceLevel(OrderSide.Sell));
    }

    [Fact]
    public void NewExternalQuantityJoinsBehindExistingLocalOrders()
    {
        MatchingEngine engine = CreateEngine();
        engine.UpdateReferenceQuote(PairId, default, new ReferenceLevel(101, 10));
        PlaceSell(engine, 1, 101, 5);
        engine.UpdateReferenceQuote(PairId, default, new ReferenceLevel(101, 20));
        PlaceSell(engine, 2, 101, 5);
        var fills = new Fill[2];

        ReferenceTradeResult first = engine.RecordReferenceTrade(PairId, OrderSide.Buy, 101, 18, fills);

        Assert.Equal(new ReferenceTradeResult(RejectionReason.None, 1), first);
        Assert.Equal(new Fill(1, 101, 5, FillRole.Maker), fills[0]);
        Assert.Equal(new ReferenceLevelState(101, 20, 7), engine.GetReferenceLevel(OrderSide.Sell));
        Assert.True(engine.TryGetOrder(2, out RestingOrder second));
        Assert.Equal(5, second.RemainingQuantity);

        ReferenceTradeResult next = engine.RecordReferenceTrade(PairId, OrderSide.Buy, 101, 9, fills);
        Assert.Equal(new ReferenceTradeResult(RejectionReason.None, 1), next);
        Assert.Equal(new Fill(2, 101, 2, FillRole.Maker), fills[0]);
    }

    [Fact]
    public void QuoteDecreaseRemovesExternalQuantityFromTheBackWithoutFillingLocalOrders()
    {
        MatchingEngine engine = CreateEngine();
        engine.UpdateReferenceQuote(PairId, default, new ReferenceLevel(101, 10));
        PlaceSell(engine, 1, 101, 5);
        engine.UpdateReferenceQuote(PairId, default, new ReferenceLevel(101, 20));
        PlaceSell(engine, 2, 101, 5);
        engine.UpdateReferenceQuote(PairId, default, new ReferenceLevel(101, 12));
        Assert.Equal(2, engine.LiveOrderCount);
        var fills = new Fill[2];

        ReferenceTradeResult result = engine.RecordReferenceTrade(PairId, OrderSide.Buy, 101, 18, fills);

        Assert.Equal(new ReferenceTradeResult(RejectionReason.None, 2), result);
        Assert.Equal(new[] { new Fill(1, 101, 5, FillRole.Maker), new Fill(2, 101, 1, FillRole.Maker) }, fills);
    }

    [Fact]
    public void BetterPricesAndFifoShareOneTradeBudgetAndRespectItsLimit()
    {
        MatchingEngine engine = CreateEngine();
        engine.UpdateReferenceQuote(PairId, default, new ReferenceLevel(101, 10));
        PlaceSell(engine, 1, 102, 10);
        PlaceSell(engine, 2, 100, 5);
        PlaceSell(engine, 3, 100, 7);
        PlaceSell(engine, 4, 103, 20);
        var fills = new Fill[3];

        ReferenceTradeResult result = engine.RecordReferenceTrade(PairId, OrderSide.Buy, 102, 40, fills);

        Assert.Equal(new ReferenceTradeResult(RejectionReason.None, 3, 8), result);
        Assert.Equal(new[]
        {
            new Fill(2, 100, 5, FillRole.Maker), new Fill(3, 100, 7, FillRole.Maker),
            new Fill(1, 102, 10, FillRole.Maker)
        }, fills);
        Assert.True(engine.TryGetOrder(4, out RestingOrder untouched));
        Assert.Equal(20, untouched.RemainingQuantity);
        Assert.Equal(40, fills.Sum(fill => fill.Quantity) + 10 + result.UnallocatedQuantity);
    }

    [Fact]
    public void RecordedSellTradesUseHighestBidsFirst()
    {
        MatchingEngine engine = CreateEngine();
        engine.UpdateReferenceQuote(PairId, new ReferenceLevel(100, 10), default);
        engine.Place(new PlaceOrder(PairId, 1, OrderSide.Buy, 101, 5, OrderType.DayLimit));
        engine.Place(new PlaceOrder(PairId, 2, OrderSide.Buy, 99, 10, OrderType.DayLimit));
        var fills = new Fill[2];

        ReferenceTradeResult result = engine.RecordReferenceTrade(PairId, OrderSide.Sell, 99, 18, fills);

        Assert.Equal(new ReferenceTradeResult(RejectionReason.None, 2), result);
        Assert.Equal(new[] { new Fill(1, 101, 5, FillRole.Maker), new Fill(2, 99, 3, FillRole.Maker) }, fills);
    }

    [Fact]
    public void NewPricePlacesExternalQuantityAheadAndQuotesAloneNeverFill()
    {
        MatchingEngine engine = CreateEngine();
        PlaceSell(engine, 1, 101, 5);
        engine.UpdateReferenceQuote(PairId, default, new ReferenceLevel(101, 10));
        Assert.Equal(new ReferenceTradeResult(RejectionReason.None),
            engine.RecordReferenceTrade(PairId, OrderSide.Buy, 101, 10, []));
        engine.UpdateReferenceQuote(PairId, new ReferenceLevel(102, 10), new ReferenceLevel(103, 10));
        Assert.True(engine.TryGetOrder(1, out _));
        engine.UpdateReferenceQuote(PairId, default, new ReferenceLevel(101, 10));
        Assert.Equal(new ReferenceLevelState(101, 10, 10), engine.GetReferenceLevel(OrderSide.Sell));
        Assert.Equal(new ReferenceTradeResult(RejectionReason.None),
            engine.RecordReferenceTrade(PairId, OrderSide.Buy, 101, 10, []));
        Assert.True(engine.TryGetOrder(1, out _));
    }

    [Fact]
    public void SelfPreventionKeepsEarlierExternalFillsAndTheRestingOrder()
    {
        MatchingEngine engine = CreateEngine();
        engine.UpdateReferenceQuote(PairId, default, new ReferenceLevel(101, 10));
        PlaceSell(engine, 1, 101, 5);
        engine.UpdateReferenceQuote(PairId, default, new ReferenceLevel(101, 20));
        var fills = new Fill[1];

        OrderResult result = engine.Place(new PlaceOrder(PairId, 2, OrderSide.Buy, 101, 30, OrderType.DayLimit), fills);

        Assert.Equal(new OrderResult(RejectionReason.None, 0, 20, CancellationReason.SelfMatchPrevention, 1), result);
        Assert.Equal(new Fill(2, 101, 10, FillRole.Taker), fills[0]);
        Assert.True(engine.TryGetOrder(1, out RestingOrder ownOrder));
        Assert.Equal(5, ownOrder.RemainingQuantity);
        Assert.Equal(new ReferenceLevelState(101, 20, 10), engine.GetReferenceLevel(OrderSide.Sell));
    }

    [Fact]
    public void BetterOwnPricePreventsTakingWorseExternalLiquidity()
    {
        MatchingEngine engine = CreateEngine();
        engine.UpdateReferenceQuote(PairId, default, new ReferenceLevel(102, 10));
        PlaceSell(engine, 1, 101, 5);

        OrderResult result = engine.Place(new PlaceOrder(PairId, 2, OrderSide.Buy, 102, 20, OrderType.DayLimit));

        Assert.Equal(new OrderResult(RejectionReason.None, 0, 20, CancellationReason.SelfMatchPrevention), result);
        Assert.Equal(new ReferenceLevelState(102, 10, 10), engine.GetReferenceLevel(OrderSide.Sell));
    }

    [Fact]
    public void CancelAndFullReductionRemoveQueueEntriesAndReuseGetsNewPriority()
    {
        MatchingEngine engine = CreateEngine();
        engine.UpdateReferenceQuote(PairId, default, new ReferenceLevel(101, 10));
        PlaceSell(engine, 1, 101, 5);
        PlaceSell(engine, 2, 101, 5);
        engine.UpdateReferenceQuote(PairId, default, new ReferenceLevel(101, 20));
        engine.Cancel(new CancelOrder(PairId, 1));
        engine.Reduce(PairId, 2, 5);
        PlaceSell(engine, 1, 101, 5);
        var fills = new Fill[1];

        ReferenceTradeResult result = engine.RecordReferenceTrade(PairId, OrderSide.Buy, 101, 23, fills);

        Assert.Equal(new ReferenceTradeResult(RejectionReason.None, 1), result);
        Assert.Equal(new Fill(1, 101, 3, FillRole.Maker), fills[0]);
    }

    [Fact]
    public void InsufficientFillOutputRejectsBeforeAnyExternalOrLocalMutation()
    {
        MatchingEngine engine = CreateEngine();
        engine.UpdateReferenceQuote(PairId, default, new ReferenceLevel(101, 10));
        Assert.Equal(new OrderResult(RejectionReason.InvalidOutputBuffer),
            engine.Place(new PlaceOrder(PairId, 1, OrderSide.Buy, 101, 5, OrderType.DayLimit)));
        PlaceSell(engine, 1, 101, 5);
        PlaceSell(engine, 2, 101, 5);
        var sentinel = new Fill(999, 999, 999, FillRole.Taker);
        Fill[] tooSmall = [sentinel];

        Assert.Equal(new ReferenceTradeResult(RejectionReason.InvalidOutputBuffer),
            engine.RecordReferenceTrade(PairId, OrderSide.Buy, 101, 20, tooSmall));

        Assert.Equal(sentinel, tooSmall[0]);
        Assert.Equal(2, engine.LiveOrderCount);
        Assert.Equal(new ReferenceLevelState(101, 10, 10), engine.GetReferenceLevel(OrderSide.Sell));
        var sufficient = new Fill[2];
        Assert.Equal(new ReferenceTradeResult(RejectionReason.None, 2),
            engine.RecordReferenceTrade(PairId, OrderSide.Buy, 101, 20, sufficient));
    }

    [Theory]
    [InlineData(-1, 10, 102, 10, RejectionReason.InvalidReferenceQuote)]
    [InlineData(100, 0, 102, 10, RejectionReason.InvalidReferenceQuote)]
    [InlineData(0, 10, 102, 10, RejectionReason.InvalidReferenceQuote)]
    [InlineData(100, 10, 102, -1, RejectionReason.InvalidReferenceQuote)]
    [InlineData(102, 10, 102, 10, RejectionReason.LockedOrCrossedReferenceQuote)]
    [InlineData(103, 10, 102, 10, RejectionReason.LockedOrCrossedReferenceQuote)]
    public void InvalidQuotesPreserveBothRecordedAndShadowState(
        long bidPrice, long bidQuantity, long askPrice, long askQuantity, RejectionReason reason)
    {
        MatchingEngine engine = CreateEngine();
        engine.UpdateReferenceQuote(PairId, new ReferenceLevel(100, 20), new ReferenceLevel(102, 30));
        var fills = new Fill[1];
        engine.Place(new PlaceOrder(PairId, 1, OrderSide.Buy, 102, 5, OrderType.DayLimit), fills);

        Assert.Equal(reason, engine.UpdateReferenceQuote(PairId,
            new ReferenceLevel(bidPrice, bidQuantity), new ReferenceLevel(askPrice, askQuantity)));
        Assert.Equal(new ReferenceLevelState(100, 20, 20), engine.GetReferenceLevel(OrderSide.Buy));
        Assert.Equal(new ReferenceLevelState(102, 30, 25), engine.GetReferenceLevel(OrderSide.Sell));
    }

    [Theory]
    [InlineData(2, OrderSide.Buy, 101, 10, RejectionReason.InvalidPairId)]
    [InlineData(PairId, (OrderSide)0, 101, 10, RejectionReason.AmbiguousReferenceTrade)]
    [InlineData(PairId, OrderSide.Buy, 0, 10, RejectionReason.InvalidPrice)]
    [InlineData(PairId, OrderSide.Buy, 101, 0, RejectionReason.InvalidQuantity)]
    public void InvalidTradesChangeNothing(long pairId, OrderSide side, long price, long quantity, RejectionReason reason)
    {
        MatchingEngine engine = CreateEngine();
        engine.UpdateReferenceQuote(PairId, default, new ReferenceLevel(101, 10));
        PlaceSell(engine, 1, 101, 5);
        var sentinel = new Fill(999, 999, 999, FillRole.Taker);
        Fill[] fills = [sentinel];

        Assert.Equal(new ReferenceTradeResult(reason), engine.RecordReferenceTrade(pairId, side, price, quantity, fills));
        Assert.Equal(sentinel, fills[0]);
        Assert.Equal(1, engine.LiveOrderCount);
        Assert.Equal(new ReferenceLevelState(101, 10, 10), engine.GetReferenceLevel(OrderSide.Sell));
    }

    [Fact]
    public void EndSessionClearsReferenceStateAndRejectsFurtherMarketInputs()
    {
        MatchingEngine engine = CreateEngine();
        engine.UpdateReferenceQuote(PairId, new ReferenceLevel(99, 10), new ReferenceLevel(101, 10));
        PlaceSell(engine, 1, 101, 5);
        var canceled = new CanceledOrder[1];

        Assert.Equal(RejectionReason.None, engine.EndSession(new EndCurrentSession(), canceled, out int count));
        Assert.Equal(1, count);
        Assert.Equal(default, engine.GetReferenceLevel(OrderSide.Buy));
        Assert.Equal(default, engine.GetReferenceLevel(OrderSide.Sell));
        Assert.Equal(RejectionReason.DayNotStarted, engine.UpdateReferenceQuote(PairId, default, default));
        Assert.Equal(new ReferenceTradeResult(RejectionReason.DayNotStarted),
            engine.RecordReferenceTrade(PairId, OrderSide.Buy, 101, 10, []));
    }

    [Fact]
    public void TradeThenQuoteDecreaseDoesNotConsumeExternalQuantityTwice()
    {
        MatchingEngine engine = CreateEngine();
        engine.UpdateReferenceQuote(PairId, default, new ReferenceLevel(101, 100));
        PlaceSell(engine, 1, 101, 20);
        Assert.Equal(new ReferenceTradeResult(RejectionReason.None),
            engine.RecordReferenceTrade(PairId, OrderSide.Buy, 101, 30, []));

        engine.UpdateReferenceQuote(PairId, default, new ReferenceLevel(101, 70));

        Assert.Equal(new ReferenceLevelState(101, 70, 70), engine.GetReferenceLevel(OrderSide.Sell));
        var fills = new Fill[1];
        Assert.Equal(new ReferenceTradeResult(RejectionReason.None, 1),
            engine.RecordReferenceTrade(PairId, OrderSide.Buy, 101, 75, fills));
        Assert.Equal(new Fill(1, 101, 5, FillRole.Maker), fills[0]);
        Assert.Equal(new OrderResult(RejectionReason.None, 10, 5, CancellationReason.Requested), engine.Reduce(PairId, 1, 5));
        Assert.Equal(new OrderResult(RejectionReason.None, 0, 10, CancellationReason.Requested), engine.Cancel(new CancelOrder(PairId, 1)));
    }

    [Fact]
    public void CancelingBetweenExternalBatchesAllowsOneAggregatedTakerFill()
    {
        MatchingEngine engine = CreateEngine();
        engine.UpdateReferenceQuote(PairId, default, new ReferenceLevel(101, 10));
        PlaceSell(engine, 1, 101, 5);
        engine.UpdateReferenceQuote(PairId, default, new ReferenceLevel(101, 20));
        engine.Cancel(new CancelOrder(PairId, 1));
        var fills = new Fill[1];

        OrderResult result = engine.Place(new PlaceOrder(PairId, 2, OrderSide.Buy, 101, 20, OrderType.DayLimit), fills);

        Assert.Equal(new OrderResult(RejectionReason.None, FillCount: 1), result);
        Assert.Equal(new Fill(2, 101, 20, FillRole.Taker), fills[0]);
    }

    private static MatchingEngine CreateEngine()
    {
        MatchingEngine engine = new(PairId);
        engine.StartSession(new StartNewSession());
        return engine;
    }

    private static void PlaceSell(MatchingEngine engine, long orderId, long price, long quantity)
    {
        Assert.Equal(new OrderResult(RejectionReason.None, RemainingQuantity: quantity),
            engine.Place(new PlaceOrder(PairId, orderId, OrderSide.Sell, price, quantity, OrderType.DayLimit)));
    }
}
