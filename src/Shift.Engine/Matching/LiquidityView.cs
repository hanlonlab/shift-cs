using Shift.Protocol.Internal;

namespace Shift.Engine.Matching;

/// <summary>Combines participant and reference queues for price/FIFO selection without merging their state.</summary>
internal sealed class LiquidityView(
    LocalOrderBook book,
    ReferenceLiquidity bids,
    ReferenceLiquidity asks)
{
    internal ReferenceLiquidity GetReference(OrderSide side) => side == OrderSide.Buy ? bids : asks;

    internal bool TryGetBest(OrderSide side, out RestingLiquidity liquidity)
    {
        ReferenceLiquidity reference = GetReference(side);
        bool hasLocal = book.TryGetBest(side, out RestingOrder local);
        if (reference.ExecutableQuantity > 0
            && (!hasLocal
                || IsBetterPrice(side, reference.PriceTicks, local.PriceTicks)
                || (reference.PriceTicks == local.PriceTicks && reference.First.OrderId == 0)))
        {
            liquidity = new RestingLiquidity(0, reference.PriceTicks, reference.First.Quantity);
            return true;
        }

        liquidity = new RestingLiquidity(local.OrderId, local.PriceTicks, local.RemainingQuantity);
        return hasLocal;
    }

    internal int CountRequiredFills(OrderSide restingSide, long limitPrice, long quantity)
    {
        int count = 0;
        foreach (RestingLiquidity liquidity in Enumerate(restingSide))
        {
            if (!IsWithinLimit(restingSide, liquidity.PriceTicks, limitPrice))
            {
                break;
            }

            if (liquidity.OrderId != 0)
            {
                count++;
            }

            quantity -= Math.Min(quantity, liquidity.Quantity);
            if (quantity == 0)
            {
                break;
            }
        }

        return count;
    }

    internal static bool IsWithinLimit(OrderSide restingSide, long price, long limitPrice)
    {
        return restingSide == OrderSide.Sell ? price <= limitPrice : price >= limitPrice;
    }

    private IEnumerable<RestingLiquidity> Enumerate(OrderSide side)
    {
        ReferenceLiquidity reference = GetReference(side);
        bool referencePending = reference.PriceTicks != 0;
        foreach (RestingOrder order in book.GetOrders(side))
        {
            if (referencePending && !IsBetterPrice(side, order.PriceTicks, reference.PriceTicks))
            {
                foreach (RestingLiquidity liquidity in EnumerateReference(reference))
                {
                    yield return liquidity;
                }

                referencePending = false;
            }

            // Orders at this price were already yielded in their modeled external queue.
            if (order.PriceTicks != reference.PriceTicks)
            {
                yield return new RestingLiquidity(order.OrderId, order.PriceTicks, order.RemainingQuantity);
            }
        }

        if (referencePending)
        {
            foreach (RestingLiquidity liquidity in EnumerateReference(reference))
            {
                yield return liquidity;
            }
        }
    }

    private IEnumerable<RestingLiquidity> EnumerateReference(ReferenceLiquidity reference)
    {
        foreach (ReferenceLiquidity.QueueEntry entry in reference.Entries)
        {
            long quantity = entry.Quantity;
            if (entry.OrderId != 0)
            {
                if (!book.TryGet(entry.OrderId, out RestingOrder order))
                {
                    throw new InvalidOperationException("The reference queue contains an order missing from the book.");
                }

                quantity = order.RemainingQuantity;
            }

            yield return new RestingLiquidity(entry.OrderId, reference.PriceTicks, quantity);
        }
    }

    private static bool IsBetterPrice(OrderSide side, long left, long right)
    {
        return side == OrderSide.Buy ? left > right : left < right;
    }
}

internal readonly record struct RestingLiquidity(long OrderId, long PriceTicks, long Quantity);
