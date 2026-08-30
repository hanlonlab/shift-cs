# Shift.Ipc

Owns the single-host transports used between processes: shared AF_UNIX datagram ingress, committed-frame delivery over loopback multicast, and length-prefixed catch-up over the replay UDS stream.

## Proposal ingress

Producers send proposals to the Sequencer over AF_UNIX datagrams. Each datagram contains exactly one complete encoded frame and is limited to 2,048 bytes. The current 35 bytes of framing overhead leave up to 2,013 bytes for the payload.

`Shift.Ipc` treats the frame as opaque bytes. Encoding and decoding belong to `Shift.Protocol`.

The Sequencer owns the receiver path. Its parent directory must already exist, and binding fails if the path is already in use. The bound path is readable and writable only by its owner.

A completed send means the kernel accepted the datagram. Only the frame later published on the committed stream confirms that the exchange accepted the proposal.

## Belongs here

- AF_UNIX endpoint paths and datagram send/receive code.
- Internal multicast and replay-stream framing.

## Does not belong here

- Business-message handling or exchange state.
- External TCP or public UDP protocols.
