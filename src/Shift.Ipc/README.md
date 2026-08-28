# Shift.Ipc

Owns the single-host transports used between processes: shared AF_UNIX datagram ingress, committed-frame delivery over loopback multicast, and length-prefixed catch-up over the replay UDS stream.

## Belongs here

- AF_UNIX endpoint paths and datagram send/receive code.
- Internal multicast and replay-stream framing.

## Does not belong here

- Business-message handling or exchange state.
- External TCP or public UDP protocols.
