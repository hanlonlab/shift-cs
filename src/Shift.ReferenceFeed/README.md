# Shift.ReferenceFeed

Reads historical reference data and publishes typed quote and trade submissions through shared AF_UNIX datagram ingress. It advances its source cursor only after observing the committed echo.

## Belongs here

- Dataset parsing, source timestamps, and stable feed record identifiers.
- UpdateReferenceQuote and RecordReferenceTrade submission production.

## Does not belong here

- Direct book mutation or synthetic participant orders.
- Sequencing, matching, persistence, or external networking.
