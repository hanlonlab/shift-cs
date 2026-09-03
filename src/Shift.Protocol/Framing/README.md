# Binary Framing

This directory owns the binary envelope around internal commands, events, and control messages. External order-entry and market-data protocols have separate envelopes.

## Version 1 layout

All multibyte values use big-endian byte order. `FrameLength` includes the header, payload, and checksum.

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 4 | Frame length |
| 4 | 1 | Version |
| 5 | 2 | Message type |
| 7 | 16 | Session ID |
| 23 | 2 | Producer ID |
| 25 | 8 | Producer sequence |
| 33 | 8 | Session sequence |
| 41 | variable | Payload |
| `FrameLength - 4` | 4 | CRC-32C of every preceding frame byte |

Payload length is `FrameLength - 45`. Minimum frame size is 45 bytes, maximum frame size is 2,048 bytes, and maximum payload size is 2,003 bytes. This pre-production layout remains version 1.

Every frame carries a nonempty 16-byte, big-endian session ID. Producer ID 0 is reserved for Archiver control frames. Submissions use a nonzero producer ID and a nonzero producer sequence. Session sequence 0 marks an unsequenced submission.

Each encoded buffer contains exactly one frame. Trailing bytes are invalid.

Only defined message types are valid in version 1. Message type zero and undefined nonzero values are rejected during encoding and decoding.

`FrameCodec` owns the generic envelope plus the role profiles for unsequenced submissions and sequenced candidates. Message-specific profiles such as `CommitThrough` live with their message codec. A successful decode returns a `CanonicalFrame` containing the original bytes, decoded header, and payload slice so downstream components do not decode it again.

## Belongs here

- Frame headers, lengths, versions, identifiers, and checksums.
- Shared frame encoding and bounds-checked decoding.

## Does not belong here

- Command or message payload definitions.
- Datagram, stream, multicast, or file I/O.
