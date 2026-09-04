# Source projects

This directory contains runtime components and shared binary contracts. Project boundaries follow responsibility; the Sequencer process hosts the separate Archiver library.

## Belongs here

- Protocol and concrete IPC code shared by runtime processes.
- Sequencer, archiver, engine, feed, and client-facing gateway components.
- Component-specific behavior kept inside the project that owns it.

## Does not belong here

- Tests, benchmarks, database migrations, or operator tools.
- Generic `Common`, transport abstraction, replay, cell, or shard projects.
