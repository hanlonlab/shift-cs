# Shift.Archiver

Owns durable storage for the exact sequenced frames. It appends and syncs the binary session log, acknowledges durable ranges, serves UDS replay, and mirrors committed data asynchronously into PostgreSQL projections.

The session log receives an explicit file path. A deployed Archiver should use persistent local storage such as `/var/lib/shift/archive/session.shiftlog`; `/run/shift` remains reserved for transient sockets. The parent directory must already exist.

The current writer creates one new continuous file and refuses to overwrite an existing file. Restart recovery and torn-tail truncation are not implemented yet.

## Belongs here

- Binary log records, commit markers, recovery scans, and replay serving.
- Asynchronous PostgreSQL journal and projection writes.

## Does not belong here

- Global ordering or exchange decisions.
- External APIs or market-data delivery.
