using Shift.Protocol.Internal;

namespace Shift.Engine.Matching;

/// <summary>
/// Models external quantity interleaved with our orders at one currently quoted price.
/// Quote decreases remove newest external quantity after absorbing prior consumption.
/// </summary>
internal sealed class ReferenceLiquidity
{
    private readonly LinkedList<QueueEntry> _queue = new();
    private readonly Dictionary<long, LinkedListNode<QueueEntry>> _localOrders = [];

    internal long PriceTicks { get; private set; }

    internal long ExecutableQuantity { get; private set; }

    internal IEnumerable<QueueEntry> Entries => _queue;

    internal QueueEntry First => _queue.First!.Value;

    internal void Update(
        ReferenceLevel previous,
        ReferenceLevel current,
        LocalOrderBook book,
        OrderSide side)
    {
        if (previous.PriceTicks != current.PriceTicks)
        {
            Clear();
            PriceTicks = current.PriceTicks;
            if (current.Quantity == 0)
            {
                return;
            }

            AddExternal(current.Quantity);
            foreach (RestingOrder order in book.GetOrdersAtPrice(side, current.PriceTicks))
            {
                AddOrder(order.OrderId, order.PriceTicks);
            }
        }
        else if (current.Quantity > previous.Quantity)
        {
            AddExternal(current.Quantity - previous.Quantity);
        }
        else if (current.Quantity < ExecutableQuantity)
        {
            RemoveNewestExternal(ExecutableQuantity - current.Quantity);
        }
    }

    internal void AddOrder(long orderId, long priceTicks)
    {
        if (PriceTicks != 0 && priceTicks == PriceTicks)
        {
            LinkedListNode<QueueEntry> node = _queue.AddLast(new QueueEntry(orderId, 0));
            _localOrders.Add(orderId, node);
        }
    }

    internal void RemoveOrder(long orderId)
    {
        if (_localOrders.Remove(orderId, out LinkedListNode<QueueEntry>? node))
        {
            _queue.Remove(node);
        }
    }

    internal void Consume(long quantity)
    {
        LinkedListNode<QueueEntry> first = _queue.First!;
        long remaining = first.Value.Quantity - quantity;
        if (remaining == 0)
        {
            _queue.RemoveFirst();
        }
        else
        {
            first.Value = first.Value with { Quantity = remaining };
        }

        ExecutableQuantity -= quantity;
    }

    internal void Clear()
    {
        _queue.Clear();
        _localOrders.Clear();
        PriceTicks = 0;
        ExecutableQuantity = 0;
    }

    private void AddExternal(long quantity)
    {
        if (_queue.Last is { Value.OrderId: 0 } last)
        {
            last.Value = last.Value with { Quantity = last.Value.Quantity + quantity };
        }
        else
        {
            _queue.AddLast(new QueueEntry(0, quantity));
        }

        ExecutableQuantity += quantity;
    }

    private void RemoveNewestExternal(long quantity)
    {
        LinkedListNode<QueueEntry>? node = _queue.Last;
        while (quantity > 0)
        {
            LinkedListNode<QueueEntry>? previous = node!.Previous;
            if (node.Value.OrderId == 0)
            {
                long removed = Math.Min(quantity, node.Value.Quantity);
                long remaining = node.Value.Quantity - removed;
                if (remaining == 0)
                {
                    _queue.Remove(node);
                }
                else
                {
                    node.Value = node.Value with { Quantity = remaining };
                }

                ExecutableQuantity -= removed;
                quantity -= removed;
            }

            node = previous;
        }
    }

    // OrderId zero denotes external quantity; local quantities remain owned by the book.
    internal readonly record struct QueueEntry(long OrderId, long Quantity);
}
