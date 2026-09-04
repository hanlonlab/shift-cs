# Sequencer live path

The Sequencer assigns order and calls the Archiver library within the same process. The Archiver owns durable storage behind `SessionArchive.CommitBatch`. Multicast carries only bytes that have crossed that durability boundary.

## Frame and session contract

Submission ingress uses the version 1 canonical frame. Producers send exactly one frame of at most 2,048 bytes per AF_UNIX datagram with a defined `MessageType`, a nonempty `SessionId`, a nonzero `ProducerId`, a nonzero `ProducerSequence`, and `SequenceId = 0`. The 45-byte framing overhead leaves up to 2,003 payload bytes. Producer ID 0 is reserved for commit control frames (`CommitThrough`). The Sequencer preserves the message type, session and producer identities, and payload, assigns the session sequence, and regenerates the frame checksum. It does not add a timestamp. Time that changes deterministic business state must arrive as an explicit sequenced command.

`ProducerId` is a 16-bit gateway identity. `ProducerSequence` is that producer's 64-bit monotonic counter. A new producer must begin at 1. Later submissions from the same producer must be contiguous (`last + 1`). The same producer sequence with the same type and payload is a retry: before commit it does nothing; after commit the stored last sequenced frame (when still in memory) and the current watermark are republished. Reusing a producer sequence with different content is fatal. A gap is rejected.

Only `StartNewSession` can open an inactive session. Its payload is empty, and its header `SessionId` becomes the current session and archive-file identity. A valid start resets the session sequence and becomes sequence 1. A session remains active until an empty-payload `EndCurrentSession` is durably committed. Producers must stop the old session before its committed end and must not submit the new session until that committed end is visible.

Every submission carries its session ID. Except for a `StartNewSession` that opens an inactive session, the ID must match the current session. The Sequencer ignores a mismatch before duplicate detection, lifecycle validation, producer cursors, batching, or sequence state can change.

Deduplication is one cursor per producer, not a map of every historical frame. Cursors are scoped to the current session and retained after its end until the next distinct start. A retry of the ended session's start (same `SessionId`) remains a committed duplicate. A distinct start may reuse producer sequence 1 or continue from `last + 1`; it clears the ended session's producer cursors.

## Batch and durability contract

The first newly accepted submission opens a batch and starts a fixed 1 ms deadline. More traffic does not extend it. The batch closes when that deadline expires, when canonical frame bytes reach the 1 MiB cap or another frame would exceed it, or when `EndCurrentSession` arrives. An end is always the final frame in its batch. There is one batch in flight and no commit pipeline.

The Sequencer passes the pending batch directly to `SessionArchive.CommitBatch` as a `ReadOnlySpan<CanonicalFrame>`. Each value carries the original sequenced bytes, decoded header, and payload. The call is synchronous and consumes the batch before the Sequencer can change it; no batch encoding, frame copies, socket transfer, or second envelope decode is needed.

The Archiver validates the complete batch before appending it. With no open log, the first frame must be an empty-payload `StartNewSession` at sequence 1; its header session ID names `/var/lib/shift/archive/{SessionId:N}.shiftlog`. With an open log, another start is invalid. Every frame must carry the open session ID, and session sequences must be contiguous. The Archiver appends the exact canonical bytes, adds its private commit marker, and calls `Flush(flushToDisk: true)` once for the batch. For an ending batch it then closes the log. Only afterward does it return the committed high-water sequence.

The Sequencer commits its pending state through that exact high-water, then multicasts every batch frame in order, followed by a canonical, empty-payload `CommitThrough` frame with the same session ID, producer ID 0, and committed high-water sequence. Every data frame multicast by the Sequencer is already durable. `CommitThrough` communicates the durable high-water and assists gap detection; it does not establish the commit for the preceding frames. An archive validation, append, flush, or multicast error terminates the live process; no uncommitted candidate is deliberately published.

`SequencerState` owns ordering, producer cursors, and pending commits. `SessionArchive` owns archive preflight, session lifecycle, and log rotation. Its private `SessionLog` owns byte writes, commit markers, and durable flushes. The executable creates and disposes the archive and transports; `SequencerServer` borrows them. The project dependency is `Shift.Sequencer` → `Shift.Archiver` → `Shift.Protocol`.

## Deployment and current boundary

| Purpose | Address |
| --- | --- |
| Submission ingress | `/run/shift/sequencer.in.sock` AF_UNIX datagram |
| Committed stream | `239.255.0.1:55000` IPv4 loopback multicast, TTL 1 |
| Archive root | `/var/lib/shift/archive` |

Run the `Shift.Sequencer` executable; there is no separate Archiver process. The socket and archive parent directories must already exist, and archive creation refuses to overwrite an existing file. This implementation is the live path only. It does not reopen logs, rebuild producer cursors after restart, truncate torn tails, serve replay, recover multicast gaps, or provide high availability.
