# Shift.IntegrationTests

This project verifies end-to-end AF_UNIX and multicast IPC and committed-frame delivery.

## Belongs here

- Socket submissions through the Sequencer, its in-process Archiver, and committed multicast.
- Exact archived bytes, commit markers, session rotation, duplicates, and archive failures.

## Does not belong here

- Component behavior that can be proven in a focused unit test.
