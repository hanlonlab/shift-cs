# Shift.IntegrationTests

`MatchingLivePathTests` runs the six-message quote/IOC session over real sockets,
checks committed engine outcomes, and compares every observed frame with the
durable journal and its final commit marker. `EngineServerTests` checks partial
fills, commit gating, output echoes, and session closure. `CommittedSessionReaderTests`
checks duplicate/stale traffic, gaps, malformed frames, and commit watermarks.

This project verifies end-to-end AF_UNIX and multicast IPC and committed-frame delivery.

## Belongs here

- Socket submissions through the Sequencer, its in-process Archiver, and committed multicast.
- Exact archived bytes, commit markers, session rotation, duplicates, and archive failures.

## Does not belong here

- Component behavior that can be proven in a focused unit test.
