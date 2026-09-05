# Shift.Benchmarks

This project measures hot paths such as frame coding, sequencing, log append, book updates, risk checks, and matching.

The committed-message benchmark covers submission ingress, Sequencer batching, the direct Archiver call and durable flush, and committed multicast delivery within the combined process.

`MatchingEngineBenchmarks.PlaceThenFillOrders` creates a session, places 10, 1,000, or 10,000 one-unit sell orders at one price, fills them with a directed reference trade, validates the exact FIFO fills, and ends the session. Each measurement includes engine and output-buffer allocation, placement, matching, validation, and cleanup. Results are per complete invocation, not per order or fill. This synthetic workload needs no market-data download and is included in the CI benchmark summary and PR report.

Run only the matching benchmark from the repository root:

```sh
dotnet run --project benchmarks/Shift.Benchmarks/Shift.Benchmarks.csproj --configuration Release -- --filter '*MatchingEngineBenchmarks*' --stopOnFirstError
```

Its reports are written to `bin/BenchmarkDotNet.Artifacts/results/MatchingEngineBenchmarks-report-github.md` and the corresponding CSV/HTML files.

[Shift.ReplayBenchmarks](../Shift.ReplayBenchmarks/README.md) separately measures recorded-session replay and order sweeps for the replay comparison. That runner requires the recorded sample and is not included in this BenchmarkDotNet report.

## Belongs here

- Small benchmarks with explicit inputs and allocation or latency results.

## Does not belong here

- Feature tests, deployment load tests, or speculative micro-optimizations.
