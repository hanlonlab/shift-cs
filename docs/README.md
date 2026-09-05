# Architecture documentation

This directory contains the authoritative descriptions of system boundaries, protocols, durability rules, and recovery behavior.

- [Sequencer live path](sequencer-live-path.md) defines the implemented session lifecycle, batching, durability gate, and multicast order.
- [Recorded replay sample](recorded-replay-sample.md) documents the verified source day, fill assumptions, acquisition commands, and deterministic results.
- [Matching-engine plan](matching-engine-plan.md) defines the proposed simulator scope, ownership boundaries, and testable delivery slices.

## Belongs here

- Architecture decisions and message-flow or failure-model documentation.

## Does not belong here

- Generated API documentation or implementation notes that contradict the code.
