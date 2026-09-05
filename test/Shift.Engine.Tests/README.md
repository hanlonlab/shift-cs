# Shift.Engine.Tests

This project verifies deterministic risk and matching behavior, reference-liquidity handling, local orders, and regenerated output events.

`MatchingEngineSessionTests` runs a complete synthetic session twice on fresh engines. The ordered inputs and fixed expected results are kept together in the test: quotes, Day/IOC orders, maker/taker fills, self-match prevention, reductions, cancellations, order-ID reuse, and session end. It checks intermediate book/reference state, exact fill and cancellation order, untouched output-buffer tails, and rejection after shutdown. It needs no downloaded market data or running services.

Run the engine tests from the repository root:

```sh
dotnet run --project test/Shift.Engine.Tests/Shift.Engine.Tests.csproj --configuration Release -- -noColor
```

## Belongs here

- Engine state-transition, reference-book, and replay-equivalence tests.

## Does not belong here

- Socket transport, archive durability, or database tests.
