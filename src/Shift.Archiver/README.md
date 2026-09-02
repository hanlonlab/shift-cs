# Shift.Archiver

Owns durable storage for the exact sequenced frames. It receives Sequencer batches over `/run/shift/archiver.sock`, appends and syncs the binary session log, and acknowledges the durable high-water mark.

The deployed archive root is `/var/lib/shift/archive`; `/run/shift` remains reserved for transient sockets. On connect the writer repairs existing `*.shiftlog` files: it locates the last valid commit marker, truncates a torn tail, and deletes logs with no committed data. An open session is reopened for append; an ended session is reported so producer cursors can be restored. `StartNewSession` creates `{SessionId:N}.shiftlog` with `FileMode.CreateNew` when no session is open. `EndCurrentSession` must be the last frame in its batch; after that batch is synced, the file is closed before the acknowledgment is sent. The parent directories must already exist.

A batch that matches the already-committed high-water is re-acknowledged without writing, so a lost `CommitThrough` is idempotent.

Replay serving and PostgreSQL projection are not implemented yet.

## Belongs here

- Binary log records, commit markers, per-session file rotation, and restart repair.

## Does not belong here

- Global ordering or exchange decisions.
- External APIs or market-data delivery.
