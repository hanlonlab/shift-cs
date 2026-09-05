using System.Globalization;
using Shift.Protocol.Internal;

namespace Shift.ReferenceFeed.Taq;

public static class TaqSampleReader
{
    public const long UnitsPerShare = 1_000_000;
    public const long TicksPerDollar = 1_000_000;

    public static Quote[] ReadQuotes(string path)
    {
        List<Quote> quotes = [];
        foreach (string[] fields in ReadRows(path,
            "Time", "Sequence_Number", "Symbol", "Best_Bid_Price", "Best_Bid_Size", "Best_Bid_Exchange",
            "Best_Offer_Price", "Best_Offer_Size", "Best_Offer_Exchange", "Best Bid Quote Condition",
            "Best_Offer_Quote_Condition", "Quote_Cancel_Correction", "LULD_NBBO_Indicator", "Security_Status_Indicator"))
        {
            RequireSymbol(fields[2]);
            var bid = new ReferenceLevel(ScaledInteger(fields[3], TicksPerDollar), ScaledInteger(fields[4], UnitsPerShare));
            var ask = new ReferenceLevel(ScaledInteger(fields[6], TicksPerDollar), ScaledInteger(fields[7], UnitsPerShare));
            bool executable = bid.PriceTicks > 0 && bid.Quantity > 0
                && ask.PriceTicks > bid.PriceTicks && ask.Quantity > 0
                && fields[9] == "R" && fields[10] == "R" && fields[11].Length == 0
                && fields[12] is "" or "A" && fields[13].Length == 0;
            quotes.Add(new Quote(Integer(fields[0]), Integer(fields[1]), bid, ask, fields[5], fields[8], executable));
        }

        RequireOrdered(quotes.Select(quote => (quote.Time, quote.Sequence)));
        return quotes.ToArray();
    }

    public static RecordedTrade[] ReadTrades(string path)
    {
        List<RecordedTrade> trades = [];
        foreach (string[] fields in ReadRows(path,
            "Time", "Sequence Number", "Symbol", "Trade Price", "Trade Volume", "Exchange",
            "Sale Condition", "Trade Correction Indicator", "Trade Stop Stock Indicator"))
        {
            RequireSymbol(fields[2]);
            bool ordinary = fields[6].All(condition => condition is ' ' or 'E' or 'F' or 'I')
                && fields[7] == "00" && fields[8] == "N";
            trades.Add(new RecordedTrade(
                Integer(fields[0]), Integer(fields[1]), ScaledInteger(fields[3], TicksPerDollar),
                ScaledInteger(fields[4], UnitsPerShare), fields[5], ordinary));
        }

        RequireOrdered(trades.Select(trade => (trade.Time, trade.Sequence)));
        return trades.ToArray();
    }

    private static IEnumerable<string[]> ReadRows(string path, params string[] columns)
    {
        using var reader = new StreamReader(path);
        string[] header = (reader.ReadLine() ?? throw new InvalidDataException("Missing TAQ header.")).Split('|');
        int[] indexes = columns.Select(column => Array.IndexOf(header, column)).ToArray();
        if (indexes.Any(index => index < 0))
        {
            throw new InvalidDataException("Unexpected TAQ sample schema.");
        }

        while (reader.ReadLine() is { } line)
        {
            string[] fields = line.Split('|');
            if (fields.Length != header.Length)
            {
                throw new InvalidDataException("Malformed TAQ row.");
            }

            yield return indexes.Select(index => fields[index].Trim()).ToArray();
        }
    }

    private static long Integer(string value) => long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);

    private static long ScaledInteger(string value, long scale)
    {
        decimal scaled = decimal.Parse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) * scale;
        if (scaled < 0 || scaled != decimal.Truncate(scaled))
        {
            throw new InvalidDataException("Sample prices and quantities must be nonnegative and fit six decimal places.");
        }

        return decimal.ToInt64(scaled);
    }

    private static void RequireSymbol(string symbol)
    {
        if (!string.Equals(symbol, "A", StringComparison.Ordinal))
        {
            throw new InvalidDataException("This sample runner expects symbol A only.");
        }
    }

    private static void RequireOrdered(IEnumerable<(long Time, long Sequence)> keys)
    {
        (long Time, long Sequence) previous = default;
        foreach ((long Time, long Sequence) key in keys)
        {
            if (key.CompareTo(previous) <= 0)
            {
                throw new InvalidDataException("TAQ event keys must be strictly ordered within each source file.");
            }

            previous = key;
        }
    }
}

public readonly record struct Quote(
    long Time, long Sequence, ReferenceLevel Bid, ReferenceLevel Ask,
    string BidExchange, string AskExchange, bool Executable);

public readonly record struct RecordedTrade(
    long Time, long Sequence, long PriceTicks, long Quantity, string Exchange, bool Ordinary);
