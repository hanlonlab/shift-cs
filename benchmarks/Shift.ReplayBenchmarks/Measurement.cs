using System.Diagnostics;

namespace Shift.ReplayBenchmarks;

internal sealed record Measurement(
    string Name, int LogicalEvents, int RunsPerBatch, double MedianMilliseconds,
    double P95BatchMillisecondsPerRun, double LogicalEventsPerSecond,
    long MedianAllocatedBytes, double[] BatchMillisecondsPerRun, long[] BatchAllocatedBytesPerRun)
{
    internal static Measurement Run(string name, int logicalEvents, Action action, int runsPerBatch)
    {
        // Allow tiered JIT/PGO to warm up. Construction and cleanup belong to each action.
        var warmup = Stopwatch.StartNew();
        do
        {
            action();
        }
        while (warmup.Elapsed < TimeSpan.FromSeconds(1));

        const int BatchCount = 30;
        double[] milliseconds = new double[BatchCount];
        long[] allocatedBytes = new long[BatchCount];
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        for (int batch = 0; batch < BatchCount; batch++)
        {
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            for (int run = 0; run < runsPerBatch; run++)
            {
                action();
            }

            milliseconds[batch] = Stopwatch.GetElapsedTime(started).TotalMilliseconds / runsPerBatch;
            allocatedBytes[batch] = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / runsPerBatch;
        }

        double[] sortedTimes = milliseconds.Order().ToArray();
        double median = (sortedTimes[14] + sortedTimes[15]) / 2;
        long[] sortedAllocations = allocatedBytes.Order().ToArray();
        return new Measurement(name, logicalEvents, runsPerBatch, median, sortedTimes[28],
            logicalEvents / (median / 1_000), sortedAllocations[15], milliseconds, allocatedBytes);
    }
}
