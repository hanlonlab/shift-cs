# Shift.Archiver

Owns durable storage for the exact sequenced frames as a library in the Sequencer process. `SessionArchive.CommitBatch(ReadOnlySpan<CanonicalFrame>)` validates the complete batch before changing any file, appends and syncs the binary session log, and returns the durable high-water mark. The Sequencer owns batching and supplies canonical frames; the Archiver checks their sequenced role and session lifecycle without decoding their bytes again.

The deployed archive root is `/var/lib/shift/archive`. An empty-payload `StartNewSession` creates `{SessionId:N}.shiftlog` from its header session ID with `FileMode.CreateNew`. Every later frame must carry that session ID and the next sequence number. `EndCurrentSession` must be the last frame in its batch; after that batch is synced, the file is closed before `CommitBatch` returns. The parent directory must already exist.

The caller owns and disposes `SessionArchive`. Its internal `SessionLog` writes the original frame bytes followed by a checksummed commit marker and calls `Flush(flushToDisk: true)`. An I/O failure faults the log and propagates to the caller. The writer refuses to overwrite an existing file. Recovery, replay serving, torn-tail truncation, and PostgreSQL projection are not implemented.

## Belongs here

- Batch lifecycle and sequence validation, binary log records, commit markers, and per-session file rotation.

## Does not belong here

- Global ordering or exchange decisions.
- Producer transport, batch sizing, or committed-frame publication.
- External APIs or market-data delivery.
