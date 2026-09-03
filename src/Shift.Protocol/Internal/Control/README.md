# Internal Control Messages

This directory owns operational messages used to coordinate protocol participants without expressing trading intent. Each message owns its payload or header profile and delegates the generic frame envelope to `FrameCodec`.

## Belongs here

- Commit-through, recovery, and lifecycle control messages.
- Control message validation and fixed field layouts.

## Does not belong here

- Trading commands or engine-produced events.
- Archiver, sequencer, replay, or socket behavior.
