# Shift.LoadGenerator

Runs a self-contained matching communication smoke scenario. It starts the real
Sequencer/Archiver and Engine host in one process, with all communication using
AF_UNIX datagrams and loopback multicast UDP.

```shell
dotnet run --project tools/Shift.LoadGenerator --configuration Release
```

The scenario starts one session, sequences a pair-1 quote (bid 99, ask 100,
quantity 10 per side), then submits participant order 1: buy 4, limit 101, IOC.
The engine processes committed inputs and submits an order update and a taker
execution of 4 at 100 ticks. An independent subscriber observes both committed
results before submitting session end. The engine also observes its own results
and finishes at sequence 6.

The command prints the six committed frames and the retained journal path under
`/tmp/shift-smoke-<id>/archive/`. It uses isolated endpoints, a fresh session ID,
and a 15-second timeout. It requires macOS or Linux and local socket access.

## Belongs here

- Repeatable submitted traffic and observed committed responses.

## Does not belong here

- Exchange logic, direct state mutation, or benchmark implementations.
