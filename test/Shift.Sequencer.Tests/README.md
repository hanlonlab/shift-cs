# Shift.Sequencer.Tests

This project verifies global ordering, timestamp assignment, message deduplication, durable-watermark publication, and pending-byte backpressure.

## Belongs here

- Deterministic sequencer state and commit-gating tests.

## Does not belong here

- Binary-log recovery, exchange behavior, or full IPC tests.
