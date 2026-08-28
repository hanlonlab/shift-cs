# Order-Entry Responses

This directory owns gateway-to-client order-entry response payloads. Each future response and its hand-written codec should occupy one file.

## Belongs here

- Acknowledgement, rejection, fill, and cancel response contracts.
- Response field offsets, lengths, and encoding rules.

## Does not belong here

- Response publication or client-session state.
- Internal engine events or projection models.
