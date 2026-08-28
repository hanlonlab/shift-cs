# Internal Events

This directory owns immutable outcomes produced from authoritative command handling. Each future event and its hand-written codec should occupy one file.

## Belongs here

- Order, execution, reference, and engine-status event payloads.
- Event field layouts, identifiers, and encoding rules.

## Does not belong here

- Input commands or operational control messages.
- Projection updates, market-data publication, or engine logic.
