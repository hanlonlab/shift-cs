# Shift.QueryApi

Owns the external TCP/HTTP query boundary. It reads asynchronous PostgreSQL projections and reports their applied sequence without participating in authoritative exchange state.

## Belongs here

- Query hosting, response models, and projection reads.
- Projection freshness and applied-sequence reporting.

## Does not belong here

- Commands, database writes, sequencing, risk, or matching.
- Direct access to Engine memory or the internal message stream.
