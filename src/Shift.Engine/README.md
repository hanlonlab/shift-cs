# Shift.Engine

Owns the deterministic in-memory exchange state machine and its process host. It consumes committed messages, applies matching, and submits derived results through sequencer ingress.

The `EngineHost/` folder owns committed stream validation, sockets, and result
submission within this project. The `Matching/` core updates state without
external I/O. See [EngineHost](EngineHost/README.md) for the live quote/IOC flow
and standalone run command.

## Belongs here

- Account, order, execution, ReferenceBook, and LocalOrderBook state.
- Deterministic command handling and result production.
- Process startup and internal IPC in `EngineHost/`.

## Does not belong here

- Wall-clock reads or external I/O in matching state transitions.
- Authoritative sequence assignment, archive persistence, or database access.
- Client protocol handling or public market-data publishing.
