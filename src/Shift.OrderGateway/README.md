# Shift.OrderGateway

Owns the external TCP order-entry boundary. It translates validated client frames into proposals sent over shared AF_UNIX datagram ingress and correlates committed multicast results back to client sessions.

## Belongs here

- TCP client sessions, wire validation, and request correlation.
- Proposal submission and internal gap recovery.

## Does not belong here

- Risk, matching, sequencing, or durable storage.
- Public market-data multicast.
