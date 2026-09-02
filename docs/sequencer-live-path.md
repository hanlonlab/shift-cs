# Sequencer live path

The Sequencer is the only process that assigns order. The Archiver is the only process that declares bytes durable. Multicast carries only bytes that have crossed that durability boundary.

## Frame and session contract

Submission ingress reuses the version 1 canonical frame unchanged. Producers send exactly one valid frame per AF_UNIX datagram with a nonempty `MessageId` and `SequenceId = 0`. The Sequencer preserves the message type, message ID, and payload, assigns the sequence, and regenerates the frame checksum. It does not add a timestamp. Time that changes deterministic business state must arrive as an explicit sequenced command.

At startup, only `StartNewSession` is valid. Its existing 16-byte `SessionId` payload is also the archive-file identity. A valid start resets the sequence and becomes sequence 1. A session remains active until an empty-payload `EndCurrentSession` is durably committed. A distinct start while active, or a non-start while inactive, is fatal. Producers must therefore stop the old session before its committed end and must not submit the new session until that committed end is visible.

Only `StartNewSession` carries the session identity. Later submissions belong to the current session implicitly, so this live path cannot identify a stale producer datagram after a session transition. Producer quiescence at the committed boundary is required.

Deduplication is scoped to the current session and retained after its end until the next distinct start. The same `MessageId`, message type, and payload is a retry: before commit it does nothing; after commit its stored sequenced frame and current watermark are republished. Reusing an ID with different content inside one session is fatal. A new distinct start clears the ended session's deduplication entries.

## Batch and durability contract

The first newly accepted submission opens a batch and starts a fixed 1 ms deadline. More traffic does not extend it. The batch closes when that deadline expires, when canonical frame bytes reach the 1 MiB cap or another frame would exceed it, or when `EndCurrentSession` arrives. An end is always the final frame in its batch. There is one batch in flight and no commit pipeline.

The Sequencer sends the batch to the Archiver as:

```text
[frame count:uint32 big-endian][canonical frame 1]...[canonical frame N]
```

The Archiver validates the complete batch before appending it. With no open log, the first frame must be `StartNewSession` at sequence 1; it creates `/var/lib/shift/archive/{SessionId:N}.shiftlog`. With an open log, another start is invalid. Frames must be contiguous. The Archiver appends the exact canonical bytes, adds its private commit marker, and calls `Flush(flushToDisk: true)` once for the batch. For an ending batch it then closes the log. Only afterward does it return a canonical, empty-payload `CommitThrough` frame with `MessageId = Guid.Empty` and the committed high-water sequence.

The Sequencer accepts only the exact high-water it is awaiting. It then multicasts every batch frame in order, followed by that same `CommitThrough` frame. Every data frame multicast by the Sequencer is already durable. `CommitThrough` communicates the durable high-water and assists gap detection and recovery; it does not establish the commit for the preceding frames. An Archiver disconnect, invalid acknowledgment, append error, flush error, or multicast error terminates the live process; no uncommitted candidate is deliberately published.

## Deployment and current boundary

| Purpose | Address |
| --- | --- |
| Submission ingress | `/run/shift/sequencer.in.sock` AF_UNIX datagram |
| Durability stream | `/run/shift/archiver.sock` AF_UNIX stream |
| Committed stream | `239.255.0.1:55000` IPv4 loopback multicast, TTL 1 |
| Archive root | `/var/lib/shift/archive` |

The socket and archive parent directories must already exist. This implementation is the live path only. It does not reopen logs, rebuild deduplication after restart, truncate torn tails, serve replay, recover multicast gaps, or provide high availability.
