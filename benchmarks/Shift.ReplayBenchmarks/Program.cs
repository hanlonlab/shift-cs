using System.Runtime.InteropServices;
using System.Text.Json;
using Shift.Engine.Matching;
using Shift.Protocol.Internal;
using Shift.Protocol.Internal.Commands;
using Shift.ReferenceFeed.Taq;
using Shift.ReferenceReplay;
using Shift.ReplayBenchmarks;

if (args.Length != 2)
{
    throw new ArgumentException("Usage: Shift.ReplayBenchmarks <sample-directory> <output-directory>");
}

string quotePath = Path.Combine(args[0], "A-nbbo-20260401.psv");
string tradePath = Path.Combine(args[0], "A-trade-20260401.psv");
Directory.CreateDirectory(args[1]);
Quote[] quotes = TaqSampleReader.ReadQuotes(quotePath);
RecordedTrade[] trades = TaqSampleReader.ReadTrades(tradePath);
ReplayReport expected = SampleReplay.Run(quotes, trades);
TapeEvent[] common = CommonTape.Prepare(quotes, trades);
CommonTape.Write(Path.Combine(args[1], "common-tape.csv"), common);
int sourceEvents = quotes.Length + trades.Length;
List<Measurement> measurements = [];
Measure("TAQ parse, warm filesystem", sourceEvents, () =>
{
    Quote[] parsedQuotes = TaqSampleReader.ReadQuotes(quotePath);
    RecordedTrade[] parsedTrades = TaqSampleReader.ReadTrades(tradePath);
    if (parsedQuotes.Length != quotes.Length || parsedTrades.Length != trades.Length)
    {
        throw new InvalidOperationException("Parsed record counts changed.");
    }
}, 1);
Measure("Sample replay, prepared arrays", sourceEvents, () => Verify(SampleReplay.Run(quotes, trades)), 10);
Measure("TAQ parse plus sample replay", sourceEvents,
    () => Verify(SampleReplay.Run(TaqSampleReader.ReadQuotes(quotePath), TaqSampleReader.ReadTrades(tradePath))), 1);
Measure("Common tape, no orders", common.Length, () => CommonTape.Replay(common), 20);
TapeEvent[] repeated = Enumerable.Range(0, 20)
    .SelectMany(day => common.Select(item => item with
    {
        TimestampNanoseconds = item.TimestampNanoseconds + day * 86_400_000_000_000L
    }))
    .ToArray();
Measure("Common tape repeated 20 times, no orders", repeated.Length, () => CommonTape.Replay(repeated), 1);
foreach (int orderCount in new[] { 10, 1_000, 10_000 })
{
    Measure($"Place then fill {orderCount} orders", orderCount + 1, () => Sweep(orderCount), orderCount == 10 ? 1_000 : 10);
}

var report = new
{
    Framework = RuntimeInformation.FrameworkDescription,
    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    LogicalProcessors = Environment.ProcessorCount,
    TimestampUtc = DateTimeOffset.UtcNow,
    SourceOutcome = expected,
    CommonQuotes = common.Count(item => item.Kind == 'Q'),
    CommonTrades = common.Count(item => item.Kind == 'T'),
    Measurements = measurements
};
string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(args[1], "shift.json"), json);
Console.WriteLine(json);

void Measure(string name, int events, Action action, int runsPerBatch)
{
    Console.Error.WriteLine($"Measuring {name}...");
    measurements.Add(Measurement.Run(name, events, action, runsPerBatch));
}

void Verify(ReplayReport result)
{
    if (result != expected)
    {
        throw new InvalidOperationException("Replay outcomes changed between iterations.");
    }
}

static void Sweep(int orderCount)
{
    var engine = new MatchingEngine(1);
    var fills = new Fill[orderCount];
    engine.StartSession(new StartNewSession());
    for (int orderId = 1; orderId <= orderCount; orderId++)
    {
        OrderResult placed = engine.Place(new PlaceOrder(1, orderId, OrderSide.Sell, 101, 1, OrderType.DayLimit));
        if (placed.RejectionReason != RejectionReason.None)
        {
            throw new InvalidOperationException("Sweep order rejected.");
        }
    }

    ReferenceTradeResult result = engine.RecordReferenceTrade(1, OrderSide.Buy, 101, orderCount, fills);
    if (result.RejectionReason != RejectionReason.None || result.FillCount != orderCount || engine.LiveOrderCount != 0)
    {
        throw new InvalidOperationException("Sweep did not fill every order.");
    }

    for (int index = 0; index < orderCount; index++)
    {
        if (fills[index].ParticipantOrderId != index + 1 || fills[index].Quantity != 1)
        {
            throw new InvalidOperationException("Sweep violated FIFO or quantity conservation.");
        }
    }

    engine.EndSession(new EndCurrentSession(), [], out _);
}
