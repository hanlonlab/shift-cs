# Shift.Sequencer.Tests

This project verifies session ordering, lifecycle enforcement, producer-sequence deduplication, durable-watermark validation, and the pending-byte cap.

## Belongs here

- Deterministic sequencer state and commit-gating tests.

## Does not belong here

- Binary-log recovery, exchange behavior, or full IPC tests.
