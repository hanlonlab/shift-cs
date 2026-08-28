# Internal Control Messages

This directory owns operational messages used to coordinate protocol participants without expressing trading intent. Each future control message and its codec should occupy one file.

## Belongs here

- Commit-through, recovery, and lifecycle control payloads.
- Control message identifiers and fixed field layouts.

## Does not belong here

- Trading commands or engine-produced events.
- Archiver, sequencer, replay, or socket behavior.
