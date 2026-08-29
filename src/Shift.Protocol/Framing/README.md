# Binary Framing

This directory owns the binary envelope around internal commands, events, and control messages. External order-entry and market-data protocols have separate envelopes.

## Version 1 layout

All multibyte values use big-endian byte order. `TotalLength` includes the header, payload, and checksum.

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 4 | Total length |
| 4 | 1 | Version |
| 5 | 2 | Message type |
| 7 | 16 | Message ID |
| 23 | 8 | Global sequence |
| 31 | variable | Payload |
| `TotalLength - 4` | 4 | CRC-32C of every preceding frame byte |

Message type zero is invalid; unknown nonzero values remain decodable for forward compatibility.

## Belongs here

- Frame headers, lengths, versions, identifiers, and checksums.
- Shared frame encoding and bounds-checked decoding.

## Does not belong here

- Command or message payload definitions.
- Datagram, stream, multicast, or file I/O.
