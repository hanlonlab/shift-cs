# Shift.Sequencer

Owns the single ordering loop. It asks `FrameCodec` to decode each submission once at ingress, ignores submissions outside the current session before deduplication or state changes, deduplicates by producer ID and producer sequence, assigns the session sequence, batches candidates for the Archiver, and multicasts only durably committed frames.

The deployed process receives submissions at `/run/shift/sequencer.in.sock`, hosts `Shift.Archiver.SessionArchive` with archive root `/var/lib/shift/archive`, and publishes committed frames to `239.255.0.1:55000` on IPv4 loopback with TTL 1. The executable creates and disposes the archive and transports; `SequencerServer` borrows them for its run.

A batch begins with the first new submission and closes at its 1 ms deadline, at 1 MiB of canonical frame bytes, or immediately on `EndCurrentSession`. The deadline does not move when more submissions arrive. The Sequencer passes the batch's `CanonicalFrame` values directly to `SessionArchive.CommitBatch`; there is one batch in flight. After that synchronous call durably flushes the batch and returns its high-water sequence, the Sequencer commits its pending state and publishes the original frames in sequence order followed by a session-tagged `CommitThrough` watermark.

`SequencerState` owns ordering, deduplication, and pending commits. `SessionArchive` owns archive validation, session files, and durable flushes. No recovery or replay is implemented.

## Belongs here

- Submission ordering and session-scoped sequence assignment.
- Bounded per-producer dedup, pending commits, durable-watermark handling, and flow control.

## Does not belong here

- Risk checks, matching, journaling, or database writes.
- External client networking.
- Wall-clock timestamps in deterministic state.
