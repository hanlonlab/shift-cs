# SHIFT C#

This repository contains a single-host replay trading platform built around one global sequence and a durably journaled message stream. Independent processes communicate internally through Unix-domain sockets and loopback multicast UDP; only client-facing gateways use TCP or external UDP multicast.

## Directory map

- `src/` contains the runtime components and shared wire contracts.
- `test/` contains focused unit and process-level integration tests.
- `database/` contains the ordered PostgreSQL schema migrations used by downstream projections.
- `benchmarks/` measures allocation and latency on the protocol, sequencer, and engine hot paths.
- `tools/` contains operator and load-generation utilities.
- `docs/` records architectural invariants, protocols, and recovery behavior.

Business state belongs in the deterministic engine, ordering belongs in the sequencer, durable records belong in the archiver, and query state belongs in PostgreSQL projections.

## Code style

Repository-wide formatting, naming, and analyzer rules live in `.editorconfig` and `Directory.Build.props`. Compiler and analyzer warnings fail the build, nullable reference types are enabled, and integral arithmetic is checked by default. Use the smallest possible `unchecked` block only when wraparound is intentional, such as in a checksum.

Authoritative exchange code follows four additional rules:

- Represent money as integer price ticks and quantities, never `float` or `double`.
- Give serialized enum members explicit numeric values, reserve zero, and never renumber or reuse a value.
- Use invariant culture for wire, journal, and replay values; compare identifiers and protocol tokens ordinally.
- Derive engine time and identifiers from sequenced input. Do not let wall-clock time, randomness, external I/O, hash codes, or unordered collection iteration affect state transitions.

Run the formatter before submitting a change:

```shell
dotnet format Shift.slnx
```

Verify formatting and compilation without changing files:

```shell
dotnet restore Shift.slnx
dotnet format Shift.slnx --verify-no-changes --no-restore
dotnet build Shift.slnx --no-restore
```
