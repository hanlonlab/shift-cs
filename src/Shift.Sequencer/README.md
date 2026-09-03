# Shift.Sequencer

Owns the single ordering loop. It asks `FrameCodec` to decode each submission once at ingress, ignores submissions outside the current session before deduplication or state changes, deduplicates by producer ID and producer sequence, assigns the session sequence, batches candidates for the Archiver, and multicasts only durably committed frames.

The deployed process receives submissions at `/run/shift/sequencer.in.sock`, connects to the Archiver at `/run/shift/archiver.sock`, and publishes committed frames to `239.255.0.1:55000` on IPv4 loopback with TTL 1.

A batch begins with the first new submission and closes after at most 1 ms, at 1 MiB of canonical frame bytes, or immediately on `EndCurrentSession`. The deadline does not move when more submissions arrive. Only one batch may await the Archiver. The Sequencer publishes the batch frames in sequence order and then its session-tagged `CommitThrough` watermark after the Archiver returns the expected session ID and exact durable high-water mark.

## Belongs here

- Submission ordering and session-scoped sequence assignment.
- Bounded per-producer dedup, pending commits, durable-watermark handling, and flow control.

## Does not belong here

- Risk checks, matching, journaling, or database writes.
- External client networking.
- Wall-clock timestamps in deterministic state.
