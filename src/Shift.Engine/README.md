# Shift.Engine

Owns the deterministic in-memory exchange state machine, with risk and matching in one process. It consumes committed messages, updates state without external I/O, and returns deterministic results through sequencer ingress.

## Belongs here

- Account, order, execution, ReferenceBook, and LocalOrderBook state.
- Deterministic command handling and result production.

## Does not belong here

- Wall-clock reads, sequence assignment, sockets, files, or database access.
- Client protocol handling or public market-data publishing.
