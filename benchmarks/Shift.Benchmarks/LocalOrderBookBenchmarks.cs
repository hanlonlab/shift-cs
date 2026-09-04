using BenchmarkDotNet.Attributes;
using Shift.Engine.Matching;

[ArtifactsPath("bin/BenchmarkDotNet.Artifacts")]
[MemoryDiagnoser]
public class LocalOrderBookBenchmarks
{
    private const long PriceTicks = 512;
    private const long FirstBenchmarkOrderId = 2_049;
    private LocalOrderBook _book = null!;
    private long _nextOrderId;

    [GlobalSetup]
    public void CreateBook()
    {
        _book = new LocalOrderBook();
        for (long priceTicks = 1; priceTicks <= 1_024; priceTicks++)
        {
            if (!_book.TryAdd(priceTicks, OrderSide.Buy, priceTicks, 100))
            {
                throw new InvalidOperationException("Could not seed the order book.");
            }
        }

        for (long orderId = 1_025; orderId < FirstBenchmarkOrderId; orderId++)
        {
            if (!_book.TryAdd(orderId, OrderSide.Buy, PriceTicks, 100))
            {
                throw new InvalidOperationException("Could not seed the order book.");
            }
        }

        _nextOrderId = FirstBenchmarkOrderId;
    }

    [Benchmark]
    public long AddAndCancelAtExistingPrice()
    {
        long orderId = _nextOrderId++;
        if (!_book.TryAdd(orderId, OrderSide.Buy, PriceTicks, 100)
            || !_book.TryCancel(orderId, out RestingOrder canceled))
        {
            throw new InvalidOperationException("The order-book benchmark lost its baseline state.");
        }

        return canceled.OrderId;
    }
}
