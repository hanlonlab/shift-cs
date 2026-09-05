namespace Shift.ReferenceReplay;

public sealed record ReplayReport(
    int QuoteEvents, int TradeEvents, int EligibleTrades, int AmbiguousTrades,
    int ExcludedTrades, long SubmittedOrders, decimal SubmittedShares, decimal FilledShares,
    decimal CanceledShares, int MakerFills, int TakerFills, decimal UnallocatedTradeShares,
    string OutcomeSha256);
