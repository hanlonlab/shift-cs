# Risk

Owns deterministic account checks and reservations used before matching. Risk state is updated in the same Engine process and ordered command flow as the books.

## Belongs here

- Buying-power checks, reservations, positions, and exposure updates.
- Risk rejection reasons and account invariants.

## Does not belong here

- Price-level matching or book data structures.
- IPC, persistence, or API validation.
