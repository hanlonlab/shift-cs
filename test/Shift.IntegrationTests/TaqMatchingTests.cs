using System.Globalization;
using Shift.Engine.Matching;
using Shift.Protocol.Internal;
using Shift.Protocol.Internal.Commands;
using Shift.ReferenceFeed.Taq;
using Xunit;

namespace Shift.IntegrationTests;

public sealed class TaqMatchingTests
{
    private const string QuoteHeader = "Time|Sequence_Number|Symbol|Best_Bid_Price|Best_Bid_Size|Best_Bid_Exchange|Best_Offer_Price|Best_Offer_Size|Best_Offer_Exchange|Best Bid Quote Condition|Best_Offer_Quote_Condition|Quote_Cancel_Correction|LULD_NBBO_Indicator|Security_Status_Indicator\n";
    private const string TradeHeader = "Time|Sequence Number|Symbol|Trade Price|Trade Volume|Exchange|Sale Condition|Trade Correction Indicator|Trade Stop Stock Indicator\n";

    [Fact]
    public void ParsedQuoteAndTradeFillAnOrderUsingExactUnitsRegardlessOfCulture()
    {
        string quotePath = Path.GetTempFileName();
        string tradePath = Path.GetTempFileName();
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            File.WriteAllText(quotePath, QuoteHeader + "093000000000001|1|A|99.999999|100|N|100.000001|100|N|R|R||A| \n");
            File.WriteAllText(tradePath, TradeHeader + "093000000000002|1|A|100.000001|110.000001|N|    |00|N\n");
            Quote quote = Assert.Single(TaqSampleReader.ReadQuotes(quotePath));
            RecordedTrade trade = Assert.Single(TaqSampleReader.ReadTrades(tradePath));
            Assert.Equal(100_000_001, quote.Ask.PriceTicks);
            Assert.Equal(100_000_000, quote.Ask.Quantity);
            Assert.Equal(110_000_001, trade.Quantity);
            Assert.True(trade.Ordinary);
            Assert.Equal(OrderSide.Buy, TradeDirection.Infer(trade, quote, false));
            MatchingEngine engine = new(1);
            engine.StartSession(new StartNewSession());
            Assert.Equal(RejectionReason.None, engine.UpdateReferenceQuote(1, quote.Bid, quote.Ask));
            Assert.Equal(RejectionReason.None, engine.Place(new PlaceOrder(
                1, 1, OrderSide.Sell, quote.Ask.PriceTicks, 20_000_000, OrderType.DayLimit)).RejectionReason);
            var fills = new Fill[1];

            ReferenceTradeResult result = engine.RecordReferenceTrade(
                1, OrderSide.Buy, trade.PriceTicks, trade.Quantity, fills);

            Assert.Equal(new ReferenceTradeResult(RejectionReason.None, 1), result);
            Assert.Equal(new Fill(1, 100_000_001, 10_000_001, FillRole.Maker), fills[0]);
            Assert.True(engine.TryGetOrder(1, out RestingOrder order));
            Assert.Equal(9_999_999, order.RemainingQuantity);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            File.Delete(quotePath);
            File.Delete(tradePath);
        }
    }

    [Theory]
    [InlineData(102, 2, false, OrderSide.Buy)]
    [InlineData(103, 2, false, OrderSide.Buy)]
    [InlineData(100, 2, false, OrderSide.Sell)]
    [InlineData(99, 2, false, OrderSide.Sell)]
    [InlineData(101, 2, false, null)]
    [InlineData(102, 2, true, null)]
    [InlineData(102, 1, false, null)]
    [InlineData(102, 0, false, null)]
    public void TradeDirectionRequiresAnEarlierQuoteAndUnambiguousPrice(
        long price, long tradeTime, bool hasSimultaneousQuote, OrderSide? expected)
    {
        var quote = new Quote(1, 1, new ReferenceLevel(100, 10), new ReferenceLevel(102, 10), "N", "N", true);
        var trade = new RecordedTrade(tradeTime, 1, price, 10, "N", true);

        Assert.Equal(expected, TradeDirection.Infer(trade, quote, hasSimultaneousQuote));
    }

    [Fact]
    public void DuplicateDeliveryAndPrecisionLossAreRejectedByTheReader()
    {
        string path = Path.GetTempFileName();
        try
        {
            const string TradeRow = "093000000000002|1|A|100.000001|.007|N|    |00|N\n";
            File.WriteAllText(path, TradeHeader + TradeRow);
            Assert.Equal(7_000, Assert.Single(TaqSampleReader.ReadTrades(path)).Quantity);

            File.WriteAllText(path, TradeHeader + TradeRow + TradeRow);
            Assert.Throws<InvalidDataException>(() => TaqSampleReader.ReadTrades(path));
            File.WriteAllText(path, TradeHeader + "093000000000002|1|A|100|.0000001|N|    |00|N\n");
            Assert.Throws<InvalidDataException>(() => TaqSampleReader.ReadTrades(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("08", "    ", false)]
    [InlineData("10", "    ", false)]
    [InlineData("01", "    ", false)]
    [InlineData("00", " O  ", false)]
    [InlineData("00", " 6  ", false)]
    [InlineData("00", "  TI", false)]
    [InlineData("00", " F I", true)]
    public void ReaderClassifiesCorrectionsAndSpecialPrintsAsIneligible(string correction, string condition, bool ordinary)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, TradeHeader + $"093000000000002|1|A|100|10|N|{condition}|{correction}|N\n");
            Assert.Equal(ordinary, Assert.Single(TaqSampleReader.ReadTrades(path)).Ordinary);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
