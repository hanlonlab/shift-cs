# Binary Framing

This directory owns the common binary envelope around protocol payloads. Framing stays independent from internal commands, events, control messages, and external message families.

## Belongs here

- Frame headers, lengths, versions, identifiers, and checksums.
- Shared frame encoding and bounds-checked decoding.

## Does not belong here

- Command or message payload definitions.
- Datagram, stream, multicast, or file I/O.
