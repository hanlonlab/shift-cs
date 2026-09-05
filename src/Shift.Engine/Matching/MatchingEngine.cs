using Shift.Protocol.Internal;
using Shift.Protocol.Internal.Commands;

namespace Shift.Engine.Matching;

/// <summary>
/// Handles one instrument and one session. The caller must supply ordered inputs
/// from a single writer and use a fresh engine for the next session.
/// </summary>
public sealed class MatchingEngine
{
    private readonly LocalOrderBook _localOrderBook = new();
    private readonly ReferenceBook _referenceBook = new();
    private readonly ReferenceLiquidity _referenceBids = new();
    private readonly ReferenceLiquidity _referenceAsks = new();
    private readonly LiquidityView _liquidity;
    private bool _hasSessionStarted;

    public MatchingEngine(long pairId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pairId);
        PairId = pairId;
        _liquidity = new LiquidityView(_localOrderBook, _referenceBids, _referenceAsks);
    }

    public long PairId { get; }

    public bool IsSessionActive { get; private set; }

    public int LiveOrderCount => _localOrderBook.Count;

    public StartSessionStatus StartSession(StartNewSession command)
    {
        if (_hasSessionStarted)
        {
            return IsSessionActive
                ? StartSessionStatus.AlreadyStarted
                : StartSessionStatus.SessionEnded;
        }

        _hasSessionStarted = true;
        IsSessionActive = true;
        return StartSessionStatus.Started;
    }

    public OrderResult Place(PlaceOrder command, Span<Fill> fills = default)
    {
        RejectionReason rejection = ValidateOrderTarget(command.PairId, command.OrderId);
        if (rejection != RejectionReason.None)
        {
            return new OrderResult(rejection);
        }

        if (command.Side is not (OrderSide.Buy or OrderSide.Sell))
        {
            return new OrderResult(RejectionReason.InvalidOrderSide);
        }

        if (command.PriceTicks <= 0)
        {
            return new OrderResult(RejectionReason.InvalidPrice);
        }

        if (command.Quantity <= 0)
        {
            return new OrderResult(RejectionReason.InvalidQuantity);
        }

        if (command.OrderType is not (OrderType.DayLimit or OrderType.ImmediateOrCancelLimit))
        {
            return new OrderResult(RejectionReason.UnsupportedOrderType);
        }

        if (_localOrderBook.TryGet(command.OrderId, out _))
        {
            return new OrderResult(RejectionReason.DuplicateOrderId);
        }

        OrderSide restingSide = Opposite(command.Side);
        if (fills.IsEmpty
            && _liquidity.TryGetBest(restingSide, out RestingLiquidity first)
            && first.OrderId == 0
            && LiquidityView.IsWithinLimit(restingSide, first.PriceTicks, command.PriceTicks))
        {
            return new OrderResult(RejectionReason.InvalidOutputBuffer);
        }

        return ExecuteIncomingOrder(command, fills);
    }

    public bool TryGetOrder(long orderId, out RestingOrder order)
    {
        return _localOrderBook.TryGet(orderId, out order);
    }

    public OrderResult Cancel(CancelOrder command)
    {
        RejectionReason rejection = ValidateOrderTarget(command.PairId, command.OrderId);
        if (rejection != RejectionReason.None)
        {
            return new OrderResult(rejection);
        }

        if (!_localOrderBook.TryCancel(command.OrderId, out RestingOrder order))
        {
            return new OrderResult(RejectionReason.UnknownOrder);
        }

        _liquidity.GetReference(order.Side).RemoveOrder(order.OrderId);
        return new OrderResult(
            RejectionReason.None,
            CanceledQuantity: order.RemainingQuantity,
            CancellationReason: CancellationReason.Requested);
    }

    public OrderResult Reduce(long pairId, long orderId, long reductionQuantity)
    {
        RejectionReason rejection = ValidateOrderTarget(pairId, orderId);
        if (rejection != RejectionReason.None)
        {
            return new OrderResult(rejection);
        }

        if (reductionQuantity <= 0)
        {
            return new OrderResult(RejectionReason.InvalidQuantity);
        }

        if (!_localOrderBook.TryGet(orderId, out RestingOrder order))
        {
            return new OrderResult(RejectionReason.UnknownOrder);
        }

        if (reductionQuantity > order.RemainingQuantity)
        {
            return new OrderResult(RejectionReason.ReductionExceedsRemainingQuantity);
        }

        long remainingQuantity = ReduceRestingOrder(orderId, order.Side, reductionQuantity);

        return new OrderResult(
            RejectionReason.None,
            RemainingQuantity: remainingQuantity,
            CanceledQuantity: reductionQuantity,
            CancellationReason: CancellationReason.Requested);
    }

    public RejectionReason UpdateReferenceQuote(long pairId, ReferenceLevel bid, ReferenceLevel ask)
    {
        RejectionReason rejection = ValidatePair(pairId);
        if (rejection != RejectionReason.None)
        {
            return rejection;
        }

        rejection = ReferenceBook.Validate(bid, ask);
        if (rejection != RejectionReason.None)
        {
            return rejection;
        }

        _referenceBids.Update(_referenceBook.Bid, bid, _localOrderBook, OrderSide.Buy);
        _referenceAsks.Update(_referenceBook.Ask, ask, _localOrderBook, OrderSide.Sell);
        _referenceBook.Update(bid, ask);
        return RejectionReason.None;
    }

    public ReferenceLevelState GetReferenceLevel(OrderSide side)
    {
        ReferenceLevel recorded = side switch
        {
            OrderSide.Buy => _referenceBook.Bid,
            OrderSide.Sell => _referenceBook.Ask,
            _ => throw new ArgumentOutOfRangeException(nameof(side))
        };

        return new ReferenceLevelState(
            recorded.PriceTicks, recorded.Quantity, _liquidity.GetReference(side).ExecutableQuantity);
    }

    /// <summary>
    /// Applies one ordinary trade with a known or explicitly inferred aggressor side.
    /// Output must fit all resulting participant fills; rejection changes no state or output.
    /// The caller excludes corrections, auctions, off-market and ambiguous prints.
    /// </summary>
    public ReferenceTradeResult RecordReferenceTrade(
        long pairId,
        OrderSide aggressorSide,
        long priceTicks,
        long quantity,
        Span<Fill> fills)
    {
        RejectionReason rejection = ValidatePair(pairId);
        if (rejection != RejectionReason.None)
        {
            return new ReferenceTradeResult(rejection);
        }

        if (aggressorSide is not (OrderSide.Buy or OrderSide.Sell))
        {
            return new ReferenceTradeResult(RejectionReason.AmbiguousReferenceTrade);
        }

        if (priceTicks <= 0)
        {
            return new ReferenceTradeResult(RejectionReason.InvalidPrice);
        }

        if (quantity <= 0)
        {
            return new ReferenceTradeResult(RejectionReason.InvalidQuantity);
        }

        OrderSide restingSide = Opposite(aggressorSide);
        if (fills.Length < _liquidity.CountRequiredFills(restingSide, priceTicks, quantity))
        {
            return new ReferenceTradeResult(RejectionReason.InvalidOutputBuffer);
        }

        int fillCount = 0;
        while (quantity > 0
            && _liquidity.TryGetBest(restingSide, out RestingLiquidity resting)
            && LiquidityView.IsWithinLimit(restingSide, resting.PriceTicks, priceTicks))
        {
            long executed = Math.Min(quantity, resting.Quantity);
            if (resting.OrderId == 0)
            {
                _liquidity.GetReference(restingSide).Consume(executed);
            }
            else
            {
                ReduceRestingOrder(resting.OrderId, restingSide, executed);
                fills[fillCount++] = new Fill(resting.OrderId, resting.PriceTicks, executed, FillRole.Maker);
            }

            quantity -= executed;
        }

        return new ReferenceTradeResult(RejectionReason.None, fillCount, quantity);
    }

    /// <summary>
    /// Cancels bids from highest to lowest price, then asks from lowest to highest,
    /// preserving FIFO within each price. Only the written prefix of the output is valid.
    /// All reported cancellations have reason <see cref="CancellationReason.EndOfDay"/>.
    /// An undersized output leaves the session, orders, and output unchanged.
    /// </summary>
    public RejectionReason EndSession(
        EndCurrentSession command,
        Span<CanceledOrder> canceledOrders,
        out int canceledOrderCount)
    {
        canceledOrderCount = 0;
        if (!IsSessionActive)
        {
            return RejectionReason.DayNotStarted;
        }

        if (canceledOrders.Length < LiveOrderCount)
        {
            return RejectionReason.InvalidOutputBuffer;
        }

        int bidCount = CancelSide(OrderSide.Buy, canceledOrders);
        int askCount = CancelSide(OrderSide.Sell, canceledOrders[bidCount..]);
        canceledOrderCount = bidCount + askCount;
        _referenceBook.Update(default, default);
        _referenceBids.Clear();
        _referenceAsks.Clear();
        IsSessionActive = false;
        return RejectionReason.None;
    }

    private RejectionReason ValidateOrderTarget(long pairId, long orderId)
    {
        RejectionReason rejection = ValidatePair(pairId);
        if (rejection != RejectionReason.None)
        {
            return rejection;
        }

        return orderId <= 0 ? RejectionReason.InvalidOrderId : RejectionReason.None;
    }

    private RejectionReason ValidatePair(long pairId)
    {
        if (!IsSessionActive)
        {
            return RejectionReason.DayNotStarted;
        }

        return pairId != PairId ? RejectionReason.InvalidPairId : RejectionReason.None;
    }

    private OrderResult ExecuteIncomingOrder(PlaceOrder command, Span<Fill> fills)
    {
        OrderSide restingSide = Opposite(command.Side);
        long remaining = command.Quantity;
        long filled = 0;
        int fillCount = 0;
        CancellationReason cancellation = CancellationReason.None;
        while (remaining > 0
            && _liquidity.TryGetBest(restingSide, out RestingLiquidity resting)
            && LiquidityView.IsWithinLimit(restingSide, resting.PriceTicks, command.PriceTicks))
        {
            if (resting.OrderId != 0)
            {
                cancellation = CancellationReason.SelfMatchPrevention;
                break;
            }

            long executed = Math.Min(remaining, resting.Quantity);
            _liquidity.GetReference(restingSide).Consume(executed);
            remaining -= executed;
            filled += executed;
            // This version has one external price per side, so one taker fill suffices.
            fills[0] = new Fill(command.OrderId, resting.PriceTicks, filled, FillRole.Taker);
            fillCount = 1;
        }

        if (remaining > 0 && cancellation == CancellationReason.None
            && command.OrderType == OrderType.ImmediateOrCancelLimit)
        {
            cancellation = CancellationReason.ImmediateOrCancel;
        }

        if (cancellation != CancellationReason.None)
        {
            return new OrderResult(RejectionReason.None, 0, remaining, cancellation, fillCount);
        }

        if (remaining > 0)
        {
            _localOrderBook.TryAdd(command.OrderId, command.Side, command.PriceTicks, remaining);
            _liquidity.GetReference(command.Side).AddOrder(command.OrderId, command.PriceTicks);
        }

        return new OrderResult(RejectionReason.None, RemainingQuantity: remaining, FillCount: fillCount);
    }

    private static OrderSide Opposite(OrderSide side) => side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;

    private long ReduceRestingOrder(long orderId, OrderSide side, long quantity)
    {
        long remainingQuantity = _localOrderBook.Reduce(orderId, quantity);
        if (remainingQuantity == 0)
        {
            _liquidity.GetReference(side).RemoveOrder(orderId);
        }

        return remainingQuantity;
    }

    private int CancelSide(OrderSide side, Span<CanceledOrder> canceledOrders)
    {
        int count = 0;
        while (_localOrderBook.TryGetBest(side, out RestingOrder order))
        {
            _localOrderBook.TryCancel(order.OrderId, out _);
            canceledOrders[count++] = new CanceledOrder(order.OrderId, order.RemainingQuantity);
        }

        return count;
    }
}
