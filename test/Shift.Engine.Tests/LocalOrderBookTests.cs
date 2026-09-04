using Shift.Engine.Matching;
using Shift.Protocol.Internal;
using Xunit;

namespace Shift.Engine.Tests;

public sealed class LocalOrderBookTests
{
    [Fact]
    public void EmptyBookHasNoBestOrders()
    {
        LocalOrderBook book = new();

        Assert.False(book.TryGetBest(OrderSide.Buy, out _));
        Assert.False(book.TryGetBest(OrderSide.Sell, out _));
    }

    [Fact]
    public void ReturnsHighestBidAndLowestAsk()
    {
        LocalOrderBook book = new();
        Assert.True(book.TryAdd(1, OrderSide.Buy, 100, 10));
        Assert.True(book.TryAdd(2, OrderSide.Buy, 101, 20));
        Assert.True(book.TryAdd(3, OrderSide.Sell, 103, 30));
        Assert.True(book.TryAdd(4, OrderSide.Sell, 102, 40));

        Assert.True(book.TryGetBest(OrderSide.Buy, out RestingOrder bid));
        Assert.True(book.TryGetBest(OrderSide.Sell, out RestingOrder ask));
        Assert.Equal(new RestingOrder(2, OrderSide.Buy, 101, 20), bid);
        Assert.Equal(new RestingOrder(4, OrderSide.Sell, 102, 40), ask);
    }

    [Fact]
    public void PreservesTimePriorityWithinPriceLevel()
    {
        LocalOrderBook book = new();
        book.TryAdd(1, OrderSide.Buy, 100, 10);
        book.TryAdd(2, OrderSide.Buy, 100, 20);

        Assert.True(book.TryGetBest(OrderSide.Buy, out RestingOrder first));
        Assert.Equal(1, first.OrderId);

        long remainingQuantity = book.Reduce(1, 10);
        Assert.Equal(0, remainingQuantity);
        Assert.True(book.TryGetBest(OrderSide.Buy, out RestingOrder second));
        Assert.Equal(2, second.OrderId);
    }

    [Fact]
    public void PartialReductionKeepsPriority()
    {
        LocalOrderBook book = new();
        book.TryAdd(1, OrderSide.Sell, 100, 10);
        book.TryAdd(2, OrderSide.Sell, 100, 20);

        long remainingQuantity = book.Reduce(1, 4);

        Assert.Equal(6, remainingQuantity);
        Assert.True(book.TryGetBest(OrderSide.Sell, out RestingOrder best));
        Assert.Equal(new RestingOrder(1, OrderSide.Sell, 100, 6), best);
    }

    [Fact]
    public void FullReductionRemovesSoleOrderAndAllowsIdReuse()
    {
        LocalOrderBook book = new();
        book.TryAdd(1, OrderSide.Sell, 100, 10);

        long remainingQuantity = book.Reduce(1, 10);

        Assert.Equal(0, remainingQuantity);
        Assert.Equal(0, book.Count);
        Assert.False(book.TryGetBest(OrderSide.Sell, out _));
        Assert.True(book.TryAdd(1, OrderSide.Buy, 99, 5));
        Assert.True(book.TryGetBest(OrderSide.Buy, out RestingOrder order));
        Assert.Equal(new RestingOrder(1, OrderSide.Buy, 99, 5), order);
    }

    [Fact]
    public void CancelsMiddleAndTailWithoutDisturbingQueue()
    {
        LocalOrderBook book = new();
        book.TryAdd(1, OrderSide.Buy, 100, 10);
        book.TryAdd(2, OrderSide.Buy, 100, 20);
        book.TryAdd(3, OrderSide.Buy, 100, 30);

        Assert.True(book.TryCancel(2, out RestingOrder canceled));
        Assert.Equal(new RestingOrder(2, OrderSide.Buy, 100, 20), canceled);
        Assert.True(book.TryAdd(4, OrderSide.Buy, 100, 40));
        Assert.True(book.TryCancel(4, out _));
        Assert.Equal(0, book.Reduce(1, 10));
        Assert.True(book.TryGetBest(OrderSide.Buy, out RestingOrder best));
        Assert.Equal(3, best.OrderId);
        Assert.Equal(1, book.Count);
    }

    [Fact]
    public void RemovingBestLevelAdvancesToNextPrice()
    {
        LocalOrderBook book = new();
        book.TryAdd(1, OrderSide.Buy, 101, 10);
        book.TryAdd(2, OrderSide.Buy, 100, 20);
        book.TryAdd(3, OrderSide.Sell, 102, 30);
        book.TryAdd(4, OrderSide.Sell, 103, 40);

        Assert.True(book.TryCancel(1, out _));
        Assert.True(book.TryCancel(3, out _));

        Assert.True(book.TryGetBest(OrderSide.Buy, out RestingOrder bid));
        Assert.True(book.TryGetBest(OrderSide.Sell, out RestingOrder ask));
        Assert.Equal(100, bid.PriceTicks);
        Assert.Equal(103, ask.PriceTicks);
    }

    [Fact]
    public void DuplicateOrderDoesNotChangeBook()
    {
        LocalOrderBook book = new();
        Assert.True(book.TryAdd(1, OrderSide.Buy, 100, 10));

        Assert.False(book.TryAdd(1, OrderSide.Sell, 90, 20));

        Assert.Equal(1, book.Count);
        Assert.True(book.TryGetBest(OrderSide.Buy, out RestingOrder order));
        Assert.Equal(new RestingOrder(1, OrderSide.Buy, 100, 10), order);
        Assert.False(book.TryGetBest(OrderSide.Sell, out _));
    }

    [Fact]
    public void FailedMutationsDoNotChangeBook()
    {
        LocalOrderBook book = new();
        book.TryAdd(1, OrderSide.Buy, 100, 10);

        Assert.False(book.TryCancel(2, out _));
        Assert.Throws<KeyNotFoundException>(() => book.Reduce(2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => book.Reduce(1, 11));

        Assert.Equal(1, book.Count);
        Assert.True(book.TryGetBest(OrderSide.Buy, out RestingOrder order));
        Assert.Equal(10, order.RemainingQuantity);
    }

    [Fact]
    public void RejectsInvalidSideAndQuantity()
    {
        LocalOrderBook book = new();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            book.TryAdd(1, (OrderSide)0, 100, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            book.TryAdd(1, OrderSide.Buy, 100, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            book.Reduce(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            book.TryGetBest((OrderSide)0, out _));
        Assert.Equal(0, book.Count);
    }
}
