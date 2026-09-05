# Replay benchmarks

Measures the existing TAQ reader and sample runner without changing their implementation. Also compares a common, prepared quote/trade stream with HftBacktest and NautilusTrader on the same machine. This is a throughput experiment, not a validation of equivalent fills.

## Run

From the repository root, fetch the [recorded sample](../../docs/recorded-replay-sample.md), build Release, and install the peer dependencies in a separate environment:

```sh
python3 tools/reference-data/fetch_nyse_sample.py
dotnet build benchmarks/Shift.ReplayBenchmarks/Shift.ReplayBenchmarks.csproj --configuration Release
uv venv --python 3.12 /tmp/shift-replay-peers
uv pip install --python /tmp/shift-replay-peers/bin/python --only-binary :all: -r benchmarks/Shift.ReplayBenchmarks/requirements.lock
/tmp/shift-replay-peers/bin/python benchmarks/Shift.ReplayBenchmarks/run.py .local-data/nyse-taq/20260401 .local-data/replay-benchmarks/comparison
```

`run.py` launches three independent processes per engine/input size, sequentially. Peer order reverses in the second process group. It checks the SHIFT outcome hash across processes and retains raw batch timings, logs, dependency versions, and the common input. Data stays under the ignored `.local-data` directory.

To measure only SHIFT:

```sh
dotnet benchmarks/Shift.ReplayBenchmarks/bin/Release/net10.0/Shift.ReplayBenchmarks.dll .local-data/nyse-taq/20260401 .local-data/replay-benchmarks/shift-only
```

## Workloads and boundaries

- **TAQ parse:** both text files, including allocation and validation, with a warm filesystem cache. No download or decompression.
- **Sample replay:** the public `SampleReplay.Run` entry point from the referenced `Shift.ReferenceReplay` project. Includes stream merge, filters, session creation/cleanup, the existing order schedule, matching, output hashing, and quantity-conservation checks. Parsing is excluded. Every iteration must equal the initial complete report.
- **Parse plus replay:** both operations above, once each. The regular CLI does two replays and also incurs process startup; this measurement does neither.
- **Common tape:** 21,054 eligible regular-session NBBO updates and 6,936 ordinary, direction-inferred trades from all venues. A shared C# normalizer retains integer millionths of dollars/shares, excludes tied or unclassifiable trades, and preserves input ordering. This is an artificial consolidated venue for measuring data processing. It is not the NYSE-only execution policy in the sample runner. No participant orders, strategy, latency, fees, output hashing, or reporting are enabled.
- **Repeated tape:** the common tape repeated twenty times with successive timestamps, in one session. This is 559,800 logical events, not twenty independent observed market days.
- **Order sweep:** construct a session, place 10/1,000/10,000 one-unit orders at one price, then fill all of them with one directed print and end the session. Includes setup, output storage, matching, FIFO validation, and cleanup. There is no external liquidity; it is a controlled scaling diagnostic, not a whole-market workload.

Common-tape timings include a fresh engine, attaching already normalized native data, processing, final-state validation, and cleanup. CSV parsing and conversion to peer-native events occur before timing. Nautilus `add_data` includes its own loading/sorting costs. HftBacktest includes data attachment and its exchange/local processing. SHIFT directly iterates an already ordered array. These differences are deliberately visible in the results; this is not an isolated matching-kernel comparison.

Each action warms for at least one second; the first peer invocation (including Numba compilation) is also excluded. There are thirty timed batches per process. The JSON records runs per batch and all batch means. Reported p95 values describe batch-average time per replay, not individual-event tail latency. Normal garbage collection remains enabled during measurement; the initial collection is outside timing. SHIFT allocation counts are managed bytes allocated on the calling thread, not retained or peak process memory. Native peer allocations are not compared.

## Peer adapters

The pinned wheels are HftBacktest 2.4.4 and NautilusTrader 1.230.0 on CPython 3.12.13/macOS ARM64. Nautilus 1.231.0 had no compatible wheel in the tested environment and its source distribution failed to build; this is not a benchmark of that release or the v2 development branch.

HftBacktest uses `HashMapMarketDepthBacktest`, `PartialFillExchange`, and `RiskAdverseQueueModel`, with zero order/feed latency. Its L2 processor does not handle the exposed `DEPTH_BBO_EVENT` flag. Each quote therefore becomes a depth clear followed by one bid and one ask update: 70,098 encoded records per day, each marked for both exchange and local processing. This prevents stale, unobserved depth. It would reset queues and is intentionally restricted to this **no-order** experiment. Final bid/ask and the last received timestamp are verified. The engine's current clock can remain at the initial time when a single `elapse` call exhausts data; the check uses `feed_latency` instead.

Nautilus uses `BacktestEngine`, an L1 book, trade execution, liquidity consumption, and queue tracking. Logging and result analysis are disabled. A generic equity-class instrument preserves six decimal places in size; its specialized `Equity` class fixes size precision to zero. Each logical event becomes one native quote or trade tick. The adapter verifies iteration count and the last cached quote. Only the pandas `Timestamp.utcnow` deprecation warning is suppressed.

These checks establish that the input was processed, not that the engines produce equivalent execution outcomes. See the [measured report](../../docs/replay-performance.md) for results and limitations.
