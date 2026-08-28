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
