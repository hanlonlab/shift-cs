using System.Security.Cryptography;
using System.Text;
using Shift.Engine.Matching;
using Shift.Protocol.Internal;
using Shift.Protocol.Internal.Commands;
using Shift.ReferenceFeed.Taq;

namespace Shift.ReferenceReplay;

/// <summary>A deterministic exercise of the engine, not a trading strategy or profitability test.</summary>
public sealed class SampleReplay : IDisposable
{
    private const long PairId = 1;
    private const long OpenTime = 93_000_000_000_000;
    private const long CloseTime = 160_000_000_000_000;
    private readonly MatchingEngine _engine = new(PairId);
    private readonly Fill[] _fills = new Fill[2];
    private readonly IncrementalHash _digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private Quote _quote;
    private long _nextOrderId;
    private long _bidOrderId;
    private long _askOrderId;
    private long _submittedQuantity;
    private long _filledQuantity;
    private long _canceledQuantity;
    private long _unallocatedTradeQuantity;
    private int _regularQuotes;
    private int _eligibleTrades;
    private int _ambiguousTrades;
    private int _makerFills;
    private int _takerFills;

    private SampleReplay()
    {
    }

    public static ReplayReport Run(Quote[] quotes, RecordedTrade[] trades)
    {
        using SampleReplay replay = new();
        replay._engine.StartSession(new StartNewSession());
        var quoteTimes = quotes.Select(quote => quote.Time).ToHashSet();
        int quoteIndex = 0;
        int tradeIndex = 0;
        // The streams have independent sequence spaces. Ties use trades before quotes;
        // tied prints are excluded from fills because their cross-stream order is unknown.
        while (quoteIndex < quotes.Length || tradeIndex < trades.Length)
        {
            if (quoteIndex < quotes.Length
                && (tradeIndex == trades.Length || quotes[quoteIndex].Time < trades[tradeIndex].Time))
            {
                replay.ApplyQuote(quotes[quoteIndex++]);
            }
            else
            {
                RecordedTrade trade = trades[tradeIndex++];
                replay.ApplyTrade(trade, quoteTimes.Contains(trade.Time));
            }
        }

        var canceled = new CanceledOrder[replay._engine.LiveOrderCount];
        RequireAccepted(replay._engine.EndSession(new EndCurrentSession(), canceled, out int count));
        for (int index = 0; index < count; index++)
        {
            replay._canceledQuantity += canceled[index].CanceledQuantity;
            replay.Hash(FormattableString.Invariant($"end,{canceled[index].OrderId},{canceled[index].CanceledQuantity}\n"));
        }

        if (replay._submittedQuantity != replay._filledQuantity + replay._canceledQuantity)
        {
            throw new InvalidOperationException("Submitted quantity did not equal filled plus canceled quantity at session end.");
        }

        return new ReplayReport(
            quotes.Length, trades.Length, replay._eligibleTrades, replay._ambiguousTrades,
            trades.Length - replay._eligibleTrades, replay._nextOrderId,
            Shares(replay._submittedQuantity), Shares(replay._filledQuantity), Shares(replay._canceledQuantity),
            replay._makerFills, replay._takerFills, Shares(replay._unallocatedTradeQuantity),
            Convert.ToHexStringLower(replay._digest.GetHashAndReset()));
    }

    private void ApplyQuote(Quote quote)
    {
        _quote = quote;
        // This runner models NYSE only. NBBO sizes identify one credited venue per side;
        // a venue that is not credited supplies no known external quantity in this sample.
        ReferenceLevel bid = quote.Executable && quote.BidExchange == "N" ? quote.Bid : default;
        ReferenceLevel ask = quote.Executable && quote.AskExchange == "N" ? quote.Ask : default;
        RequireAccepted(_engine.UpdateReferenceQuote(PairId, bid, ask));
        if (quote.Time < OpenTime || quote.Time >= CloseTime || !quote.Executable)
        {
            return;
        }

        _regularQuotes++;
        if (_regularQuotes % 100 != 0)
        {
            return;
        }

        CancelIfLive(_bidOrderId);
        CancelIfLive(_askOrderId);
        if (_regularQuotes % 500 == 0)
        {
            Submit(OrderSide.Buy, quote.Ask.PriceTicks, 5, OrderType.ImmediateOrCancelLimit);
            Submit(OrderSide.Sell, quote.Bid.PriceTicks, 5, OrderType.ImmediateOrCancelLimit);
        }

        _bidOrderId = Submit(OrderSide.Buy, quote.Bid.PriceTicks, 10, OrderType.DayLimit);
        _askOrderId = Submit(OrderSide.Sell, quote.Ask.PriceTicks, 10, OrderType.DayLimit);
    }

    private void ApplyTrade(RecordedTrade trade, bool timestampTie)
    {
        if (trade.Time < OpenTime || trade.Time >= CloseTime || !trade.Ordinary
            || trade.Exchange != "N" || trade.PriceTicks <= 0 || trade.Quantity <= 0)
        {
            return;
        }

        OrderSide? inferredSide = TradeDirection.Infer(trade, _quote, timestampTie);
        if (inferredSide is not { } side)
        {
            _ambiguousTrades++;
            return;
        }

        // Only model queue demand when the corresponding NYSE quote is observable.
        if ((side == OrderSide.Buy && _quote.AskExchange != "N")
            || (side == OrderSide.Sell && _quote.BidExchange != "N"))
        {
            return;
        }

        ReferenceTradeResult result = _engine.RecordReferenceTrade(
            PairId, side, trade.PriceTicks, trade.Quantity, _fills);
        RequireAccepted(result.RejectionReason);
        _eligibleTrades++;
        _unallocatedTradeQuantity += result.UnallocatedQuantity;
        RecordFills(result.FillCount);
        Hash(FormattableString.Invariant($"trade,{trade.Time},{trade.Sequence},{result.UnallocatedQuantity}\n"));
    }

    private long Submit(OrderSide side, long price, long quantity, OrderType type)
    {
        long orderId = ++_nextOrderId;
        quantity *= TaqSampleReader.UnitsPerShare;
        OrderResult result = _engine.Place(new PlaceOrder(PairId, orderId, side, price, quantity, type), _fills);
        RequireAccepted(result.RejectionReason);
        _submittedQuantity += quantity;
        _canceledQuantity += result.CanceledQuantity;
        RecordFills(result.FillCount);
        Hash(FormattableString.Invariant($"order,{orderId},{result.RemainingQuantity},{result.CanceledQuantity},{(byte)result.CancellationReason}\n"));
        return orderId;
    }

    private void CancelIfLive(long orderId)
    {
        if (_engine.TryGetOrder(orderId, out _))
        {
            OrderResult result = _engine.Cancel(new CancelOrder(PairId, orderId));
            RequireAccepted(result.RejectionReason);
            _canceledQuantity += result.CanceledQuantity;
            Hash(FormattableString.Invariant($"cancel,{orderId},{result.CanceledQuantity}\n"));
        }
    }

    private void RecordFills(int count)
    {
        for (int index = 0; index < count; index++)
        {
            Fill fill = _fills[index];
            _filledQuantity += fill.Quantity;
            if (fill.Role == FillRole.Maker)
            {
                _makerFills++;
            }
            else
            {
                _takerFills++;
            }

            Hash(FormattableString.Invariant($"fill,{fill.ParticipantOrderId},{fill.PriceTicks},{fill.Quantity},{(byte)fill.Role}\n"));
        }
    }

    private void Hash(string value) => _digest.AppendData(Encoding.UTF8.GetBytes(value));

    private static decimal Shares(long quantity) => (decimal)quantity / TaqSampleReader.UnitsPerShare;

    private static void RequireAccepted(RejectionReason reason)
    {
        if (reason != RejectionReason.None)
        {
            throw new InvalidOperationException($"Unexpected engine rejection: {reason}.");
        }
    }

    public void Dispose() => _digest.Dispose();
}
