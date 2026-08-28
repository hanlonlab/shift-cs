# Shift.MarketDataGateway

Consumes committed internal multicast and builds the public market-data feed. It is the only process that sends external UDP multicast and serves TCP gap fill for its separate public sequence and log.

## Belongs here

- Public feed encoding, sequence assignment, multicast, and gap fill.
- The public market-data session log.

## Does not belong here

- Private commands, risk state, or matching decisions.
- Exposure of the canonical internal archive.
