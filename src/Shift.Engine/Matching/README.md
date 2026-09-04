# Matching

Owns deterministic order matching and book state. Participant orders live in LocalOrderBook, while replayed market observations update ReferenceBook without becoming synthetic participant orders.

`LocalOrderBook` holds the residual limit orders for one instrument in price-time priority. The deterministic Engine is its single writer; matching and trade generation remain outside the book.

## Belongs here

- Orders, price levels, matching rules, and executions.
- LocalOrderBook and ReferenceBook behavior.

## Does not belong here

- Account-wide risk policy or client sessions.
- Feed parsing, journaling, database access, or networking.
