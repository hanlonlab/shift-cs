# Shift.Protocol

This project owns the fixed, hand-written binary contracts and codecs shared by SHIFT processes. Each future command or message belongs in one clearly named file.

## Belongs here

- Binary frame and payload definitions.
- Explicit `Span` and `BinaryPrimitives` codecs.

## Does not belong here

- Socket, transport, persistence, or process-lifecycle code.
- Matching, risk, projection, or other business logic.
