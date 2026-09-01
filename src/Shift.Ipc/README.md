# Shift.Ipc

Owns the single-host transports used between processes: shared AF_UNIX datagram ingress, the Sequencer-to-Archiver AF_UNIX stream, and committed-frame delivery over loopback multicast.

## Proposal ingress

Producers send proposals to the Sequencer over AF_UNIX datagrams. Each datagram contains exactly one complete encoded frame and is limited to 2,048 bytes. The current 35 bytes of framing overhead leave up to 2,013 bytes for the payload.

`Shift.Ipc` treats the frame as opaque bytes. Encoding and decoding belong to `Shift.Protocol`.

The Sequencer owns the receiver path. Its parent directory must already exist, and binding fails if the path is already in use. The bound path is readable and writable only by its owner.

A completed send means the kernel accepted the datagram. Only the frame later published on the committed stream confirms that the exchange accepted the proposal.

## Durability stream

The Archiver listens on `/run/shift/archiver.sock`. Each batch is `[count:uint32 big-endian][canonical frame 1]...[canonical frame N]`; each frame already begins with its own big-endian length. The total canonical frame bytes are limited to 1 MiB. The Archiver replies with one canonical `CommitThrough` frame.

## Committed multicast

The Sequencer sends one canonical frame per datagram to `239.255.0.1:55000` over IPv4 loopback with TTL 1. It sends committed batch frames in order followed by their `CommitThrough` watermark. UDP loss recovery and replay are outside the current live-only implementation.

## Belongs here

- AF_UNIX endpoint paths and datagram send/receive code.
- Sequencer-to-Archiver stream transfer and internal multicast.

## Does not belong here

- Business-message handling or exchange state.
- External TCP or public UDP protocols.
