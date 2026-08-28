# Endpoints

Contains thin Query API endpoints that validate query inputs, read PostgreSQL projections, and map results to external responses.

## Belongs here

- Route handlers and query-specific response mapping.
- Projection freshness fields returned to clients.

## Does not belong here

- Business rules, projection writes, or exchange-state mutation.
- Sequencer, Engine, archive, or IPC coordination.
