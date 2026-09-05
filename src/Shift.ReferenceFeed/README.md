# Shift.ReferenceFeed

Contains a reader for the verified NYSE Daily TAQ sample and recorded-data trade-direction inference. Prices and quantities are converted exactly to integer millionths; source keys are validated before replay. See [the sample notes](../../docs/recorded-replay-sample.md) for units, filtering, and limitations.

Publication of typed quote/trade submissions through shared AF_UNIX datagram ingress is planned. A future live reader must advance its source cursor only after observing the committed echo.

## Belongs here

- Dataset parsing, source timestamps, and stable feed record identifiers.
- UpdateReferenceQuote and RecordReferenceTrade submission production.

## Does not belong here

- Direct book mutation or synthetic participant orders.
- Sequencing, matching, persistence, or external networking.
