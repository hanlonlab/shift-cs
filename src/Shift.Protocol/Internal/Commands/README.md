# Internal Commands

This directory owns state-changing requests admitted to the authoritative sequenced stream. Each future command and its hand-written codec should occupy one file.

`UpdateReferenceQuote` (message type 9) replaces the current bid and ask for one positive `PairId`. Its fixed 40-byte payload contains five big-endian signed 64-bit values: pair ID, bid price ticks, bid quantity, ask price ticks, and ask quantity. An absent side is exactly `(0, 0)`; a present side requires positive price and quantity. When both sides are present, the bid must be below the ask. These are recorded reference quantities; simulated fills do not alter the quote payload.

## Belongs here

- Order, reference-data, and simulation-clock command payloads.
- Command field layouts, identifiers, and decoding rules.

## Does not belong here

- Derived events or operational control messages.
- Sequencing, risk checks, or matching behavior.
