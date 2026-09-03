# Internal Control Messages

This directory owns operational messages used to coordinate protocol participants without expressing trading intent. Control messages with payload schemas belong here; header-only frame profiles such as `CommitThrough` belong in `FrameCodec`.

## Belongs here

- Recovery and lifecycle control payloads.
- Control payload identifiers and fixed field layouts.

## Does not belong here

- Trading commands or engine-produced events.
- Archiver, sequencer, replay, or socket behavior.
