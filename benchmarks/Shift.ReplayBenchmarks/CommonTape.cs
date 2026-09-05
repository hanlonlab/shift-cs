using System.Globalization;
using Shift.Engine.Matching;
using Shift.Protocol.Internal;
using Shift.Protocol.Internal.Commands;
using Shift.ReferenceFeed.Taq;

namespace Shift.ReplayBenchmarks;

/// <summary>Identical input for a data-only throughput comparison, with no participant orders.</summary>
internal static class CommonTape
{
    internal static TapeEvent[] Prepare(Quote[] quotes, RecordedTrade[] trades)
    {
        List<TapeEvent> events = [];
        var quoteTimes = quotes.Select(quote => quote.Time).ToHashSet();
        Quote current = default;
        int quoteIndex = 0;
        int tradeIndex = 0;
        while (quoteIndex < quotes.Length || tradeIndex < trades.Length)
        {
            if (quoteIndex < quotes.Length
                && (tradeIndex == trades.Length || quotes[quoteIndex].Time < trades[tradeIndex].Time))
            {
                current = quotes[quoteIndex++];
                if (IsRegularSession(current.Time) && current.Executable)
                {
                    events.Add(new TapeEvent('Q', Timestamp(current.Time),
                        current.Bid.PriceTicks, current.Bid.Quantity, current.Ask.PriceTicks, current.Ask.Quantity, default));
                }
            }
            else
            {
                RecordedTrade trade = trades[tradeIndex++];
                OrderSide? side = TradeDirection.Infer(trade, current, quoteTimes.Contains(trade.Time));
                if (IsRegularSession(trade.Time) && trade.Ordinary && trade.PriceTicks > 0
                    && trade.Quantity > 0 && side is { } aggressor)
                {
                    events.Add(new TapeEvent('T', Timestamp(trade.Time), trade.PriceTicks, trade.Quantity, 0, 0, aggressor));
                }
            }
        }

        return events.ToArray();
    }

    internal static void Write(string path, TapeEvent[] events)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("kind,timestamp_ns,price,quantity,ask_price,ask_quantity,side");
        foreach (TapeEvent item in events)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{item.Kind},{item.TimestampNanoseconds},{item.Price},{item.Quantity},{item.AskPrice},{item.AskQuantity},{(byte)item.Side}"));
        }
    }

    internal static void Replay(TapeEvent[] events)
    {
        var engine = new MatchingEngine(1);
        engine.StartSession(new StartNewSession());
        foreach (TapeEvent item in events)
        {
            RejectionReason rejection = item.Kind == 'Q'
                ? engine.UpdateReferenceQuote(1, new ReferenceLevel(item.Price, item.Quantity),
                    new ReferenceLevel(item.AskPrice, item.AskQuantity))
                : engine.RecordReferenceTrade(1, item.Side, item.Price, item.Quantity, []).RejectionReason;
            if (rejection != RejectionReason.None)
            {
                throw new InvalidOperationException($"Common tape rejected: {rejection}.");
            }
        }

        TapeEvent lastQuote = events.Last(item => item.Kind == 'Q');
        if (engine.GetReferenceLevel(OrderSide.Buy).PriceTicks != lastQuote.Price
            || engine.GetReferenceLevel(OrderSide.Sell).PriceTicks != lastQuote.AskPrice
            || engine.LiveOrderCount != 0)
        {
            throw new InvalidOperationException("Common tape final state did not match.");
        }

        engine.EndSession(new EndCurrentSession(), [], out _);
    }

    private static bool IsRegularSession(long time) => time >= 93_000_000_000_000 && time < 160_000_000_000_000;

    private static long Timestamp(long packedTime)
    {
        long seconds = packedTime / 1_000_000_000;
        long nanoseconds = packedTime % 1_000_000_000;
        long secondsSinceMidnight = (seconds / 10_000 * 60 + seconds / 100 % 100) * 60 + seconds % 100;
        // 2026-04-01 New York midnight is 04:00 UTC (EDT).
        long midnight = new DateTimeOffset(2026, 4, 1, 4, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        return (midnight + secondsSinceMidnight) * 1_000_000_000 + nanoseconds;
    }
}

internal readonly record struct TapeEvent(
    char Kind, long TimestampNanoseconds, long Price, long Quantity,
    long AskPrice, long AskQuantity, OrderSide Side);
