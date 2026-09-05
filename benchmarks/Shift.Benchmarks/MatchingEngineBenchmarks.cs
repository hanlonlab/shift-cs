using BenchmarkDotNet.Attributes;
using Shift.Engine.Matching;
using Shift.Protocol.Internal;
using Shift.Protocol.Internal.Commands;

[ArtifactsPath("bin/BenchmarkDotNet.Artifacts")]
[MemoryDiagnoser]
public class MatchingEngineBenchmarks
{
    [Params(10, 1_000, 10_000)]
    public int OrderCount { get; set; }

    [Benchmark]
    public int PlaceThenFillOrders()
    {
        // Each invocation owns a complete session, including allocation, validation, and cleanup.
        var engine = new MatchingEngine(1);
        var fills = new Fill[OrderCount];
        engine.StartSession(new StartNewSession());
        for (int orderId = 1; orderId <= OrderCount; orderId++)
        {
            OrderResult placed = engine.Place(new PlaceOrder(1, orderId, OrderSide.Sell, 101, 1, OrderType.DayLimit));
            if (placed != new OrderResult(RejectionReason.None, RemainingQuantity: 1))
            {
                throw new InvalidOperationException("The matching benchmark did not rest the submitted order.");
            }
        }

        ReferenceTradeResult result = engine.RecordReferenceTrade(1, OrderSide.Buy, 101, OrderCount, fills);
        if (result != new ReferenceTradeResult(RejectionReason.None, OrderCount) || engine.LiveOrderCount != 0)
        {
            throw new InvalidOperationException("The matching benchmark did not fill every order.");
        }

        for (int index = 0; index < OrderCount; index++)
        {
            if (fills[index] != new Fill(index + 1, 101, 1, FillRole.Maker))
            {
                throw new InvalidOperationException("The matching benchmark produced an unexpected FIFO fill.");
            }
        }

        if (engine.EndSession(new EndCurrentSession(), [], out int canceledCount) != RejectionReason.None
            || canceledCount != 0 || engine.IsSessionActive)
        {
            throw new InvalidOperationException("The matching benchmark did not end its session cleanly.");
        }

        return result.FillCount;
    }
}
