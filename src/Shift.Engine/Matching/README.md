# Matching

Owns order matching and book state. Participant orders live in LocalOrderBook. Recorded market quotes belong in ReferenceBook, with simulated consumption tracked separately so participant activity does not change historical data.

LocalOrderBook currently stores remaining limit orders for one instrument, using best price first and FIFO within each price. Matching and trade generation remain outside the book.

MatchingEngine handles one instrument (`PairId`) and one session. It supports Day and IOC limit orders, lookup, cancellation, quantity reduction, immediate executions against recorded quotes, and maker fills from directed reference trades. Engine tests exercise these behaviors, and the [recorded replay sample](../../../docs/recorded-replay-sample.md) runs a complete day twice with identical outcomes.

## Supported behavior

- Ordinary limit orders that keep their unfilled quantity in the book until filled, canceled, or session end.
- Limit orders that execute immediately and cancel any remainder.
- Cancellation and quantity reduction.
- Best price first, then oldest order first at the same price.
- One simulated participant whose orders cannot trade with each other.

Hidden orders, post-only behavior, and proportional allocation are outside this first version. Keep the existing FIFO structure. See the [simplified matching-engine plan](../../../docs/matching-engine-plan.md) for the data prerequisites and fill assumptions, and its [delivery slices](../../../docs/matching-engine-plan.md#delivery-slices) for ownership boundaries and completion criteria. The sequence is order lifecycle, execution against a quote, waiting-order fills, then a repeatable recorded session.

## Library API

Construct `new MatchingEngine(pairId)` with a positive instrument ID, then call `StartSession(new StartNewSession())`. Repeated start preserves active state. An ended engine cannot restart; construct a fresh engine for the next session.

| Method | Result |
| --- | --- |
| `Place(command, fills)` | `OrderResult`: remaining/canceled quantities, reasons, and fill count. Day residuals rest; IOC residuals cancel. At most one taker fill is produced at the current external price. |
| `TryGetOrder(orderId, out order)` | An immutable snapshot of the live order. |
| `Cancel(command)` / `Reduce(pairId, orderId, quantity)` | Requested cancellation amounts. Partial reduction preserves FIFO. |
| `UpdateReferenceQuote(pairId, bid, ask)` | Typed validation outcome. A default level means an absent side; a present level needs positive price and quantity. Quotes alone never fill resting orders. |
| `GetReferenceLevel(side)` | Raw observed quantity and remaining simulated quantity, separately. |
| `RecordReferenceTrade(pairId, aggressorSide, price, quantity, fills)` | Maker fill count and unallocated trade quantity. The reader supplies directed, eligible trade demand. |
| `EndSession(command, canceledOrders, out count)` | Cancels highest bids first, then lowest asks, preserving FIFO, and clears all state. Each cancellation has the implied `EndOfDay` reason. |

The caller owns and reuses output spans. Only the reported prefix is valid. Insufficient capacity rejects before any state/output mutation. Invalid requests return existing typed rejection reasons; `OrderResult` quantities describe successful instructions only. `PostOnlyLimit` and unknown types return `UnsupportedOrderType`. Wrong-instrument requests return `InvalidPairId`.

All methods require a single writer and ordered, deduplicated input. Live order IDs are unique; completed IDs may be reused with new priority. The caller must prevent delayed instructions from targeting a reused ID. Session-related rejection names retain the existing protocol's `DayNotStarted` terminology.

ReferenceBook stores only accepted recorded levels; ReferenceLiquidity owns separate external quantity and queue positions. LiquidityView selects price/FIFO priority across them without combining their state. New external quantity joins behind existing local orders. Self-match prevention cancels the incoming remainder when its traversal reaches our order, preserving any earlier external fills.

Historical queue position is estimated. Disappearing/returning prices reset external capacity and queue position; unseen depth is not modeled. Source conversion, venue selection and trade-direction assumptions are documented with the sample. Live sequencer publication remains future work.

## Belongs here

- Orders, price levels, matching rules, and executions.
- Participant book state and recorded-market fill modeling.

## Does not belong here

- Account-wide risk policy or client sessions.
- Feed parsing, journaling, database access, or networking.
