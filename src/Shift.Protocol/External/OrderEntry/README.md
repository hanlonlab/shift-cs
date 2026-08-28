# External Order Entry

This directory owns the client-facing binary order-entry protocol. Requests, responses, and session messages remain separated by their wire-level roles.

## Belongs here

- Client order-entry contracts and codecs.
- Order-entry protocol identifiers and fixed field layouts.

## Does not belong here

- Internal sequenced commands or engine events.
- TCP sessions, authentication decisions, or gateway behavior.
