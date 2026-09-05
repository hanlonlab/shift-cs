# Reference replay

Runs the verified NYSE Daily TAQ sample for symbol `A` on 2026-04-01 through MatchingEngine. It loads both source files, runs the same deterministic schedule twice, compares the complete reports, and verifies that all submitted quantity is filled or canceled at session end.

From the repository root:

```shell
python3 tools/reference-data/fetch_nyse_sample.py
dotnet run --project tools/Shift.ReferenceReplay/Shift.ReferenceReplay.csproj --configuration Release -- .local-data/nyse-taq/20260401
```

The full download, extraction checksums, input assumptions, fixed schedule, and observed results are described in [the sample notes](../../docs/recorded-replay-sample.md). Raw data stays in the ignored `.local-data/` directory. The runner needs no credentials, database, or live sequencer.

`SampleReplay.Run(Quote[], RecordedTrade[])` is the reusable entry point and returns
a `ReplayReport`. The CLI and replay benchmarks call the same implementation;
benchmarks reference this project directly.

This is an execution and reproducibility exercise. The modeled venue is NYSE (`N`), and known external quantity is limited to NYSE quotes credited in the NBBO. The complete consolidated tape is read, but only eligible NYSE trades contribute fill demand. The report counts eligible/excluded prints and separately identifies ambiguous direction among otherwise ordinary NYSE trades. `ExcludedTrades` includes `AmbiguousTrades`.

`TaqSampleReader` and `TradeDirection` in Shift.ReferenceFeed own source conversion and direction inference. The runner owns delivery order and the fixed participant order schedule. MatchingEngine owns every fill and book mutation. No strategy framework, live publication, account balances, or profitability calculations are included.
