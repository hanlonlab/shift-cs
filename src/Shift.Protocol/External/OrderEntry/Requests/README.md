# Order-Entry Requests

This directory owns client-to-gateway order-entry request payloads. Each future request and its hand-written codec should occupy one file.

## Belongs here

- Place, cancel, and replace request contracts.
- Request field offsets, lengths, and decoding rules.

## Does not belong here

- Gateway validation or request routing.
- Internal commands produced from accepted requests.
