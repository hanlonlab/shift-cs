# Shift.Archiver

Owns durable storage for the exact sequenced frames. It appends and syncs the binary session log, acknowledges durable ranges, serves UDS replay, and mirrors committed data asynchronously into PostgreSQL projections.

## Belongs here

- Binary log records, commit markers, recovery scans, and replay serving.
- Asynchronous PostgreSQL journal and projection writes.

## Does not belong here

- Global ordering or exchange decisions.
- External APIs or market-data delivery.
