using System.Globalization;
using Shift.LoadGenerator;
using Shift.Protocol.Framing;

if (args.Length != 0)
{
    Console.Error.WriteLine("Usage: Shift.LoadGenerator (runs the one-quote IOC smoke scenario)");
    return 1;
}

string directory = Path.Combine("/tmp", $"shift-smoke-{Guid.NewGuid():N}");
try
{
    MatchingSmokeResult result = await MatchingSmokeScenario.RunAsync(directory);
    Console.WriteLine($"Session {result.SessionId}");
    foreach (CanonicalFrame frame in result.CommittedFrames)
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Committed {frame.Header.SequenceId}: {frame.Header.MessageType} (producer {frame.Header.ProducerId}/{frame.Header.ProducerSequence})"));
    }

    Console.WriteLine("IOC order 1 bought 4 at 100 ticks; remaining 0, canceled 0.");
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"Engine applied through {result.EngineAppliedThrough}; observed {result.EngineObservedResults} committed results."));
    Console.WriteLine($"Archive: {result.ArchivePath}");
    return 0;
}
catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
{
    Console.Error.WriteLine($"Smoke scenario failed: {exception.Message}");
    return 1;
}
