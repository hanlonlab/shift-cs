# Internal Commands

This directory owns state-changing requests admitted to the authoritative sequenced stream. Each future command and its hand-written codec should occupy one file.

## Belongs here

- Order, reference-data, and simulation-clock command payloads.
- Command field layouts, identifiers, and decoding rules.

## Does not belong here

- Derived events or operational control messages.
- Sequencing, risk checks, or matching behavior.
