using System.Text.Json;
using Shift.ReferenceFeed.Taq;
using Shift.ReferenceReplay;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: Shift.ReferenceReplay <directory containing A-nbbo-20260401.psv and A-trade-20260401.psv>");
    return 1;
}

try
{
    Quote[] quotes = TaqSampleReader.ReadQuotes(Path.Combine(args[0], "A-nbbo-20260401.psv"));
    RecordedTrade[] trades = TaqSampleReader.ReadTrades(Path.Combine(args[0], "A-trade-20260401.psv"));
    ReplayReport first = SampleReplay.Run(quotes, trades);
    ReplayReport second = SampleReplay.Run(quotes, trades);
    if (first != second)
    {
        throw new InvalidOperationException("Replaying the same inputs produced different outcomes.");
    }

    Console.WriteLine(JsonSerializer.Serialize(first, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception exception) when (exception is IOException or FormatException or OverflowException or InvalidOperationException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
