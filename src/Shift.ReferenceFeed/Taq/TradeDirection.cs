using Shift.Protocol.Internal;

namespace Shift.ReferenceFeed.Taq;

public static class TradeDirection
{
    /// <summary>
    /// Infers direction using only an earlier executable NBBO. Inside-spread prints and
    /// trades sharing a timestamp with a quote remain ambiguous. This is an estimate,
    /// not an aggressor flag supplied by the tape.
    /// </summary>
    public static OrderSide? Infer(RecordedTrade trade, Quote quote, bool hasSimultaneousQuote)
    {
        if (hasSimultaneousQuote || !quote.Executable || quote.Time >= trade.Time)
        {
            return null;
        }

        if (trade.PriceTicks >= quote.Ask.PriceTicks)
        {
            return OrderSide.Buy;
        }

        return trade.PriceTicks <= quote.Bid.PriceTicks ? OrderSide.Sell : null;
    }
}
