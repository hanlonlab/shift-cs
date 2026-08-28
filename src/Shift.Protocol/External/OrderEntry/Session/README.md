# Order-Entry Session Messages

This directory owns order-entry messages that establish and maintain the external client session. Each future session message and its codec should occupy one file.

## Belongs here

- Logon, logout, heartbeat, and session-status contracts.
- Session message field layouts and identifiers.

## Does not belong here

- Authentication policy, credentials, or connection state.
- TCP reads, writes, timers, or reconnect behavior.
