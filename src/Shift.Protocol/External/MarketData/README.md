# External Market Data

This directory owns the binary messages published by the Market Data Gateway to external consumers. Each future market-data message and its codec should occupy one file.

## Belongs here

- External book, trade, and status message payloads.
- Fixed field layouts and message type identifiers.

## Does not belong here

- Internal engine events or projections.
- UDP publication, packet scheduling, or retransmission logic.
