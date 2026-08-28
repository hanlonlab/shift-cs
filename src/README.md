# Source projects

This directory contains one folder per runtime process or deliberately shared binary contract. Project boundaries follow operational ownership rather than generic technical layers.

## Belongs here

- Protocol and concrete IPC code shared by runtime processes.
- Sequencer, archiver, engine, feed, and client-facing gateway processes.
- Process-specific behavior kept inside the process that owns it.

## Does not belong here

- Tests, benchmarks, database migrations, or operator tools.
- Generic `Common`, transport abstraction, replay, cell, or shard projects.
