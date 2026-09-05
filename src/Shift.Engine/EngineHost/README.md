# EngineHost

Hosts the deterministic matching core within the `Shift.Engine` executable
against the Sequencer's committed multicast stream, then sends derived results
to the same AF_UNIX ingress. The matching core remains free of sockets and other
external I/O.

- `EngineServer` dispatches committed commands to matching and coordinates session lifecycle.
- `CommittedSessionReader` owns contiguous delivery and commit-watermark checks.
- `EngineResultPublisher` owns result encoding, submission producer sequences,
  and pending results until their committed echoes arrive.
- `Program.cs` in the project root owns process startup and socket disposal.

This slice supports one configured session, one instrument, one simulated
participant, reference quote updates, and limit IOC orders. Other order types
produce an `UnsupportedOrderType` update; other command types stop the host.
Each IOC produces `OrderUpdated` followed by its `TradeExecuted`, if filled.
The host assigns only its own contiguous producer sequence; the Sequencer assigns
the authoritative sequence. Producer ID 2 is reserved for engine outputs.

`CommittedSessionReader` decodes each copied frame once, then buffers it until a matching `CommitThrough`
confirms a contiguous batch. It ignores other sessions and already consumed
sequences, rejects gaps and conflicting pending duplicates, and bounds pending
data to the Sequencer's 1 MiB batch limit. The host observes its own committed
outputs without applying them to matching state a second time. Session end is
accepted only after all generated results have been observed committed.

Run the self-contained example from the repository root:

```shell
dotnet run --project tools/Shift.LoadGenerator --configuration Release
```

To run the host separately alongside the existing Sequencer executable:

```shell
dotnet run --project src/Shift.Engine --configuration Release -- <session-id>
```

This uses pair 1, engine producer 2, `/run/shift/sequencer.in.sock`, and loopback
multicast `239.255.0.1:55000`. Start the host before submitting `StartNewSession`
with that session ID. Use a fresh host for each session.

This is a live communication slice. It has no UDP gap recovery, restart/replay,
output retry, or multiple-engine routing. A missing frame or protocol error
stops processing; loss of a trailing watermark requires caller cancellation or
timeout. Ending the session must be coordinated by the submitter after observing
the engine results, as the smoke scenario does.
