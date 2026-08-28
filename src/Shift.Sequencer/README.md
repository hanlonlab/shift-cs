# Shift.Sequencer

Owns the single ordering loop. It deduplicates proposals, assigns the global sequence and timestamp, sends candidates to the Archiver, and multicasts only durably committed frames.

## Belongs here

- Proposal ordering, sequence assignment, and the sequencer clock.
- Pending commits, durable-watermark handling, and bounded flow control.

## Does not belong here

- Risk checks, matching, journaling, or database writes.
- External client networking.
