using Shift.Protocol.Internal;

namespace Shift.Engine.Matching;

public class LocalOrderBook
{
    private static readonly IComparer<long> _descendingPrices =
        Comparer<long>.Create(static (left, right) => right.CompareTo(left));

    private readonly Dictionary<long, OrderNode> _orders = [];
    private readonly SortedDictionary<long, PriceLevel> _bids = new(_descendingPrices);
    private readonly SortedDictionary<long, PriceLevel> _asks = [];
    private PriceLevel? _bestBid;
    private PriceLevel? _bestAsk;

    public int Count => _orders.Count;

    internal IEnumerable<RestingOrder> GetOrders(OrderSide side)
    {
        foreach (PriceLevel level in GetLevels(side).Values)
        {
            for (OrderNode? order = level.Head; order is not null; order = order.Next)
            {
                yield return order.ToRestingOrder();
            }
        }
    }

    internal IEnumerable<RestingOrder> GetOrdersAtPrice(OrderSide side, long priceTicks)
    {
        if (GetLevels(side).TryGetValue(priceTicks, out PriceLevel? level))
        {
            for (OrderNode? order = level.Head; order is not null; order = order.Next)
            {
                yield return order.ToRestingOrder();
            }
        }
    }

    public bool TryAdd(long orderId, OrderSide side, long priceTicks, long quantity)
    {
        SortedDictionary<long, PriceLevel> levels = GetLevels(side);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

        if (_orders.ContainsKey(orderId))
        {
            return false;
        }

        if (!levels.TryGetValue(priceTicks, out PriceLevel? level))
        {
            level = new PriceLevel(priceTicks);
            levels.Add(priceTicks, level);
            UpdateBest(side, level);
        }

        var order = new OrderNode(orderId, side, quantity, level)
        {
            Previous = level.Tail
        };

        if (level.Tail is null)
        {
            level.Head = order;
        }
        else
        {
            level.Tail.Next = order;
        }

        level.Tail = order;
        _orders.Add(orderId, order);
        return true;
    }

    public bool TryGet(long orderId, out RestingOrder order)
    {
        if (!_orders.TryGetValue(orderId, out OrderNode? node))
        {
            order = default;
            return false;
        }

        order = node.ToRestingOrder();
        return true;
    }

    public bool TryGetBest(OrderSide side, out RestingOrder order)
    {
        PriceLevel? level = side switch
        {
            OrderSide.Buy => _bestBid,
            OrderSide.Sell => _bestAsk,
            _ => throw new ArgumentOutOfRangeException(nameof(side))
        };

        if (level is null)
        {
            order = default;
            return false;
        }

        order = level.Head!.ToRestingOrder();
        return true;
    }

    public bool TryCancel(long orderId, out RestingOrder order)
    {
        if (!_orders.Remove(orderId, out OrderNode? node))
        {
            order = default;
            return false;
        }

        order = node.ToRestingOrder();
        Unlink(node);
        return true;
    }

    public long Reduce(long orderId, long reductionQuantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reductionQuantity);
        OrderNode order = _orders[orderId];
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            reductionQuantity,
            order.RemainingQuantity);

        order.RemainingQuantity -= reductionQuantity;
        if (order.RemainingQuantity == 0)
        {
            _orders.Remove(orderId);
            Unlink(order);
        }

        return order.RemainingQuantity;
    }

    private SortedDictionary<long, PriceLevel> GetLevels(OrderSide side)
    {
        return side switch
        {
            OrderSide.Buy => _bids,
            OrderSide.Sell => _asks,
            _ => throw new ArgumentOutOfRangeException(nameof(side))
        };
    }

    private void UpdateBest(OrderSide side, PriceLevel level)
    {
        if (side == OrderSide.Buy && (_bestBid is null || level.PriceTicks > _bestBid.PriceTicks))
        {
            _bestBid = level;
        }
        else if (side == OrderSide.Sell
            && (_bestAsk is null || level.PriceTicks < _bestAsk.PriceTicks))
        {
            _bestAsk = level;
        }
    }

    private void Unlink(OrderNode order)
    {
        PriceLevel level = order.Level;
        if (order.Previous is null)
        {
            level.Head = order.Next;
        }
        else
        {
            order.Previous.Next = order.Next;
        }

        if (order.Next is null)
        {
            level.Tail = order.Previous;
        }
        else
        {
            order.Next.Previous = order.Previous;
        }

        if (level.Head is not null)
        {
            return;
        }

        SortedDictionary<long, PriceLevel> levels = GetLevels(order.Side);
        levels.Remove(level.PriceTicks);

        if (order.Side == OrderSide.Buy && ReferenceEquals(level, _bestBid))
        {
            _bestBid = FindBest(levels);
        }
        else if (order.Side == OrderSide.Sell && ReferenceEquals(level, _bestAsk))
        {
            _bestAsk = FindBest(levels);
        }
    }

    private static PriceLevel? FindBest(SortedDictionary<long, PriceLevel> levels)
    {
        foreach (KeyValuePair<long, PriceLevel> level in levels)
        {
            return level.Value;
        }

        return null;
    }

    private class PriceLevel(long priceTicks)
    {
        internal long PriceTicks { get; } = priceTicks;

        internal OrderNode? Head { get; set; }

        internal OrderNode? Tail { get; set; }
    }

    private class OrderNode(
        long orderId,
        OrderSide side,
        long remainingQuantity,
        PriceLevel level)
    {
        internal long OrderId { get; } = orderId;

        internal OrderSide Side { get; } = side;

        internal long RemainingQuantity { get; set; } = remainingQuantity;

        internal PriceLevel Level { get; } = level;

        internal OrderNode? Previous { get; set; }

        internal OrderNode? Next { get; set; }

        internal RestingOrder ToRestingOrder()
        {
            return new RestingOrder(OrderId, Side, Level.PriceTicks, RemainingQuantity);
        }
    }
}
