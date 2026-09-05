using Shift.Engine.Matching;
using Shift.Protocol.Internal;
using Shift.Protocol.Internal.Commands;
using Xunit;

namespace Shift.Engine.Tests;

public sealed class MatchingEngineSessionTests
{
    private const long PairId = 7;
    private static readonly Fill _fillSentinel = new(999, 999, 999, FillRole.Taker);

    [Fact]
    public void CompleteSessionMatchesExpectedOutputsOnEveryReplay()
    {
        // Both fresh engines must match the same fixed expectations, including intermediate state.
        ReplaySession();
        ReplaySession();
    }

    private static void ReplaySession()
    {
        var engine = new MatchingEngine(PairId);
        var fills = new Fill[4];

        Place(new PlaceOrder(PairId, 1, OrderSide.Buy, 99, 5, OrderType.DayLimit),
            new OrderResult(RejectionReason.DayNotStarted));
        AssertBook(engine);
        Assert.Equal(StartSessionStatus.Started, engine.StartSession(new StartNewSession()));
        Assert.True(engine.IsSessionActive);
        Assert.Equal(RejectionReason.None, engine.UpdateReferenceQuote(
            PairId, new ReferenceLevel(99, 20), new ReferenceLevel(101, 10)));

        // Join both sides, including two asks behind the ten external shares at 101.
        Place(new PlaceOrder(PairId, 1, OrderSide.Sell, 101, 8, OrderType.DayLimit),
            new OrderResult(RejectionReason.None, RemainingQuantity: 8));
        Place(new PlaceOrder(PairId, 2, OrderSide.Sell, 101, 7, OrderType.DayLimit),
            new OrderResult(RejectionReason.None, RemainingQuantity: 7));
        Place(new PlaceOrder(PairId, 3, OrderSide.Sell, 102, 9, OrderType.DayLimit),
            new OrderResult(RejectionReason.None, RemainingQuantity: 9));
        Place(new PlaceOrder(PairId, 4, OrderSide.Buy, 99, 6, OrderType.DayLimit),
            new OrderResult(RejectionReason.None, RemainingQuantity: 6));
        Place(new PlaceOrder(PairId, 5, OrderSide.Buy, 98, 5, OrderType.DayLimit),
            new OrderResult(RejectionReason.None, RemainingQuantity: 5));
        AssertBook(engine,
            new RestingOrder(1, OrderSide.Sell, 101, 8),
            new RestingOrder(2, OrderSide.Sell, 101, 7),
            new RestingOrder(3, OrderSide.Sell, 102, 9),
            new RestingOrder(4, OrderSide.Buy, 99, 6),
            new RestingOrder(5, OrderSide.Buy, 98, 5));

        // Take four shares, then append six newly quoted shares behind our existing asks.
        Place(new PlaceOrder(PairId, 6, OrderSide.Buy, 101, 4, OrderType.ImmediateOrCancelLimit),
            new OrderResult(RejectionReason.None, FillCount: 1),
            new Fill(6, 101, 4, FillRole.Taker));
        Assert.Equal(new ReferenceLevelState(101, 10, 6), engine.GetReferenceLevel(OrderSide.Sell));
        Assert.Equal(RejectionReason.None, engine.UpdateReferenceQuote(
            PairId, new ReferenceLevel(99, 20), new ReferenceLevel(101, 16)));
        Assert.Equal(new ReferenceLevelState(101, 16, 12), engine.GetReferenceLevel(OrderSide.Sell));

        // The next buyer takes the old external remainder, then cancels before self-matching.
        Place(new PlaceOrder(PairId, 7, OrderSide.Buy, 102, 20, OrderType.DayLimit),
            new OrderResult(RejectionReason.None, 0, 14, CancellationReason.SelfMatchPrevention, 1),
            new Fill(7, 101, 6, FillRole.Taker));
        Assert.Equal(new ReferenceLevelState(101, 16, 6), engine.GetReferenceLevel(OrderSide.Sell));
        Assert.Equal(new OrderResult(RejectionReason.None, 5, 3, CancellationReason.Requested),
            engine.Reduce(PairId, 1, 3));
        Assert.Equal(new OrderResult(RejectionReason.None, 0, 7, CancellationReason.Requested),
            engine.Cancel(new CancelOrder(PairId, 2)));
        Assert.False(engine.TryGetOrder(2, out _));
        Place(new PlaceOrder(PairId, 2, OrderSide.Sell, 101, 4, OrderType.DayLimit),
            new OrderResult(RejectionReason.None, RemainingQuantity: 4));
        AssertBook(engine,
            new RestingOrder(1, OrderSide.Sell, 101, 5),
            new RestingOrder(2, OrderSide.Sell, 101, 4),
            new RestingOrder(3, OrderSide.Sell, 102, 9),
            new RestingOrder(4, OrderSide.Buy, 99, 6),
            new RestingOrder(5, OrderSide.Buy, 98, 5));

        // Order 1 retains priority; reused order 2 waits behind the new external shares.
        Trade(OrderSide.Buy, 102, 8, new ReferenceTradeResult(RejectionReason.None, 1),
            new Fill(1, 101, 5, FillRole.Maker));
        AssertBook(engine,
            new RestingOrder(2, OrderSide.Sell, 101, 4),
            new RestingOrder(3, OrderSide.Sell, 102, 9),
            new RestingOrder(4, OrderSide.Buy, 99, 6),
            new RestingOrder(5, OrderSide.Buy, 98, 5));
        Assert.Equal(new ReferenceLevelState(101, 16, 3), engine.GetReferenceLevel(OrderSide.Sell));

        // The next print consumes three external shares, order 2, then the worse-priced ask.
        Trade(OrderSide.Buy, 102, 12, new ReferenceTradeResult(RejectionReason.None, 2),
            new Fill(2, 101, 4, FillRole.Maker),
            new Fill(3, 102, 5, FillRole.Maker));
        AssertBook(engine,
            new RestingOrder(3, OrderSide.Sell, 102, 4),
            new RestingOrder(4, OrderSide.Buy, 99, 6),
            new RestingOrder(5, OrderSide.Buy, 98, 5));
        Assert.Equal(new ReferenceLevelState(99, 20, 20), engine.GetReferenceLevel(OrderSide.Buy));
        Assert.Equal(new ReferenceLevelState(101, 16, 0), engine.GetReferenceLevel(OrderSide.Sell));
        Assert.Equal(RejectionReason.None, engine.UpdateReferenceQuote(
            PairId, new ReferenceLevel(99, 20), new ReferenceLevel(101, 16)));
        Assert.Equal(new ReferenceLevelState(101, 16, 0), engine.GetReferenceLevel(OrderSide.Sell));

        // A sell print spends its budget on the external bid before partially filling order 4.
        Trade(OrderSide.Sell, 98, 24, new ReferenceTradeResult(RejectionReason.None, 1),
            new Fill(4, 99, 4, FillRole.Maker));
        AssertBook(engine,
            new RestingOrder(3, OrderSide.Sell, 102, 4),
            new RestingOrder(4, OrderSide.Buy, 99, 2),
            new RestingOrder(5, OrderSide.Buy, 98, 5));
        Assert.Equal(new ReferenceLevelState(99, 20, 0), engine.GetReferenceLevel(OrderSide.Buy));
        Assert.Equal(new OrderResult(RejectionReason.None, 0, 2, CancellationReason.Requested),
            engine.Reduce(PairId, 4, 2));
        Assert.Equal(new OrderResult(RejectionReason.None, 0, 5, CancellationReason.Requested),
            engine.Cancel(new CancelOrder(PairId, 5)));
        AssertBook(engine, new RestingOrder(3, OrderSide.Sell, 102, 4));

        // A new quote price resets external capacity. A Day seller takes at 100 and rests at 99.
        Assert.Equal(RejectionReason.None, engine.UpdateReferenceQuote(
            PairId, new ReferenceLevel(100, 12), new ReferenceLevel(103, 8)));
        Place(new PlaceOrder(PairId, 8, OrderSide.Sell, 99, 17, OrderType.DayLimit),
            new OrderResult(RejectionReason.None, RemainingQuantity: 5, FillCount: 1),
            new Fill(8, 100, 12, FillRole.Taker));
        AssertBook(engine,
            new RestingOrder(3, OrderSide.Sell, 102, 4),
            new RestingOrder(8, OrderSide.Sell, 99, 5));
        Assert.Equal(new ReferenceLevelState(100, 12, 0), engine.GetReferenceLevel(OrderSide.Buy));
        Assert.Equal(new ReferenceLevelState(103, 8, 8), engine.GetReferenceLevel(OrderSide.Sell));

        // Price priority beats arrival order; the print cannot consume the external ask at 103.
        Trade(OrderSide.Buy, 102, 12, new ReferenceTradeResult(RejectionReason.None, 2, 3),
            new Fill(8, 99, 5, FillRole.Maker),
            new Fill(3, 102, 4, FillRole.Maker));
        AssertBook(engine);
        Assert.Equal(new ReferenceLevelState(103, 8, 8), engine.GetReferenceLevel(OrderSide.Sell));
        Place(new PlaceOrder(PairId, 9, OrderSide.Buy, 104, 11, OrderType.ImmediateOrCancelLimit),
            new OrderResult(RejectionReason.None, 0, 3, CancellationReason.ImmediateOrCancel, 1),
            new Fill(9, 103, 8, FillRole.Taker));
        AssertBook(engine);
        Assert.Equal(new ReferenceLevelState(103, 8, 0), engine.GetReferenceLevel(OrderSide.Sell));

        // Leave multiple prices and FIFO peers on each side for deterministic end-of-day cleanup.
        Place(new PlaceOrder(PairId, 10, OrderSide.Buy, 100, 6, OrderType.DayLimit),
            new OrderResult(RejectionReason.None, RemainingQuantity: 6));
        Place(new PlaceOrder(PairId, 11, OrderSide.Buy, 99, 7, OrderType.DayLimit),
            new OrderResult(RejectionReason.None, RemainingQuantity: 7));
        Place(new PlaceOrder(PairId, 12, OrderSide.Buy, 100, 8, OrderType.DayLimit),
            new OrderResult(RejectionReason.None, RemainingQuantity: 8));
        Place(new PlaceOrder(PairId, 13, OrderSide.Sell, 103, 9, OrderType.DayLimit),
            new OrderResult(RejectionReason.None, RemainingQuantity: 9));
        Place(new PlaceOrder(PairId, 14, OrderSide.Sell, 102, 10, OrderType.DayLimit),
            new OrderResult(RejectionReason.None, RemainingQuantity: 10));
        Place(new PlaceOrder(PairId, 15, OrderSide.Sell, 102, 11, OrderType.DayLimit),
            new OrderResult(RejectionReason.None, RemainingQuantity: 11));
        AssertBook(engine,
            new RestingOrder(10, OrderSide.Buy, 100, 6),
            new RestingOrder(11, OrderSide.Buy, 99, 7),
            new RestingOrder(12, OrderSide.Buy, 100, 8),
            new RestingOrder(13, OrderSide.Sell, 103, 9),
            new RestingOrder(14, OrderSide.Sell, 102, 10),
            new RestingOrder(15, OrderSide.Sell, 102, 11));
        var cancellationSentinel = new CanceledOrder(999, 999);
        var canceled = new CanceledOrder[7];
        Array.Fill(canceled, cancellationSentinel);
        Assert.Equal(RejectionReason.None, engine.EndSession(new EndCurrentSession(), canceled, out int count));
        Assert.Equal(6, count);
        Assert.Equal(new[]
        {
            new CanceledOrder(10, 6), new CanceledOrder(12, 8), new CanceledOrder(11, 7),
            new CanceledOrder(14, 10), new CanceledOrder(15, 11), new CanceledOrder(13, 9),
            cancellationSentinel
        }, canceled);
        Assert.False(engine.IsSessionActive);
        AssertBook(engine);
        Assert.Equal(default, engine.GetReferenceLevel(OrderSide.Buy));
        Assert.Equal(default, engine.GetReferenceLevel(OrderSide.Sell));
        for (long orderId = 1; orderId <= 15; orderId++)
        {
            Assert.False(engine.TryGetOrder(orderId, out _));
        }

        Place(new PlaceOrder(PairId, 16, OrderSide.Buy, 99, 5, OrderType.DayLimit),
            new OrderResult(RejectionReason.DayNotStarted));
        Trade(OrderSide.Buy, 103, 1, new ReferenceTradeResult(RejectionReason.DayNotStarted));
        Assert.Equal(RejectionReason.DayNotStarted, engine.UpdateReferenceQuote(
            PairId, new ReferenceLevel(99, 20), new ReferenceLevel(101, 10)));
        Assert.Equal(StartSessionStatus.SessionEnded, engine.StartSession(new StartNewSession()));
        Assert.False(engine.IsSessionActive);
        AssertBook(engine);
        Assert.Equal(default, engine.GetReferenceLevel(OrderSide.Buy));
        Assert.Equal(default, engine.GetReferenceLevel(OrderSide.Sell));

        void Place(PlaceOrder input, OrderResult expected, params Fill[] expectedFills)
        {
            Array.Fill(fills, _fillSentinel);
            OrderResult actual = engine.Place(input, fills);
            Assert.Equal(expected, actual);
            AssertFills(fills, actual.FillCount, expectedFills);
        }

        void Trade(OrderSide aggressorSide, long price, long quantity,
            ReferenceTradeResult expected, params Fill[] expectedFills)
        {
            Array.Fill(fills, _fillSentinel);
            ReferenceTradeResult actual = engine.RecordReferenceTrade(PairId, aggressorSide, price, quantity, fills);
            Assert.Equal(expected, actual);
            AssertFills(fills, actual.FillCount, expectedFills);
        }
    }

    private static void AssertFills(Fill[] output, int count, Fill[] expected)
    {
        Assert.Equal(expected, output[..count]);
        Assert.All(output[count..], fill => Assert.Equal(_fillSentinel, fill));
    }

    private static void AssertBook(MatchingEngine engine, params RestingOrder[] expected)
    {
        Assert.Equal(expected.Length, engine.LiveOrderCount);
        foreach (RestingOrder order in expected)
        {
            Assert.True(engine.TryGetOrder(order.OrderId, out RestingOrder actual));
            Assert.Equal(order, actual);
        }
    }
}
