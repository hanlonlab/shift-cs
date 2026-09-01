# Shift.Archiver

Owns durable storage for the exact sequenced frames. It receives Sequencer batches over `/run/shift/archiver.sock`, appends and syncs the binary session log, and acknowledges the durable high-water mark.

The deployed archive root is `/var/lib/shift/archive`; `/run/shift` remains reserved for transient sockets. `StartNewSession` creates `{SessionId:N}.shiftlog` with `FileMode.CreateNew`. `EndCurrentSession` must be the last frame in its batch; after that batch is synced, the file is closed before the acknowledgment is sent. The parent directories must already exist.

The current writer refuses to overwrite an existing file. Restart recovery, replay serving, torn-tail truncation, and PostgreSQL projection are not implemented yet.

## Belongs here

- Binary log records, commit markers, and per-session file rotation.

## Does not belong here

- Global ordering or exchange decisions.
- External APIs or market-data delivery.
