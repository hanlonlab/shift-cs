# Matching

Owns deterministic order matching and book state. Participant orders live in LocalOrderBook, while replayed market observations update ReferenceBook without becoming synthetic participant orders.

`LocalOrderBook` holds the residual limit orders for one instrument in price-time priority. The deterministic Engine is its single writer; matching and trade generation remain outside the book.

`MatchingEngine` currently owns one `LocalOrderBook` and accepts `StartNewSession` through `StartSession`. The first command activates the session; another start returns `AlreadyStarted` without changing state. Matching, session end, and matching-result production are not implemented yet.

## Pro-rata specification

Pro-rata allocation works one price level at a time after all better prices have been consumed.

For each selected price:

1. Put executable interest in deterministic queue order.
2. Apply self-trade prevention and keep only the executable prefix.
3. Combine eligible anonymous reference slices into one candidate.
4. Snapshot each local order's remaining quantity and the anonymous candidate's executable shadow quantity once.
5. Set the allocatable quantity to the smaller of the incoming remainder and the total snapshotted weight.
6. Give each candidate `floor(allocatable quantity * weight / total weight)` using exact integer arithmetic.
7. Give the remaining rounding units, one per candidate, in queue order. Earlier priority wins; stable interest identity breaks an exact tie. The anonymous aggregate takes the queue position and stable identity of its oldest eligible slice.
8. Apply the complete allocation without recalculating weights between fills.

There is no guaranteed minimum allocation. A candidate whose proportional share is below one unit receives nothing unless it receives a rounding unit. Raw observed reference quantity is never a weight; previously consumed shadow quantity cannot be allocated again. Anonymous slices are combined so their internal fragmentation cannot create extra rounding opportunities. An allocation to the aggregate consumes its eligible slices oldest first, and the next input takes the aggregate's rank from the oldest surviving slice.

### Weighting, rounding, and rounding residual

At 100 ticks, suppose an external aggressor encounters local orders of 50 and 30 units, followed by 20 units of executable anonymous reference liquidity. For an allocatable quantity of 17:

| Candidate | Weight | Exact share | Base allocation |
|---|---:|---:|---:|
| Local order 1 | 50 | 8.5 | 8 |
| Local order 2 | 30 | 5.1 | 5 |
| Anonymous reference | 20 | 3.4 | 3 |

The base allocations total 16. The one rounding unit goes to local order 1 because it has earlier queue priority, producing final allocations of 9, 5, and 3.

With weights of 1 and 99 and an allocatable quantity of 1, both base allocations are zero. The single rounding unit goes to the earlier candidate. No separate minimum-allocation rule is applied.

For an external reference aggressor and an interleaved queue of anonymous 1, local A 1, anonymous 1, and local B 1, the anonymous slices become one weight-2 candidate at the first slice's position. An allocatable quantity of 2 gives base allocations of 1 anonymous, 0 to A, and 0 to B. The one rounding unit also goes to the anonymous aggregate because its oldest slice is first, producing 2, 0, and 0. Combining the slices gives anonymous liquidity only one residual position rather than one position per internal slice.

If the incoming remainder exceeds total weight, every candidate is filled and the unmatched quantity continues to the next price. After the last compatible price, a Day residual rests, an IOC residual cancels, and a reference-trade residual is returned as unallocated observed quantity.

### Snapshot timing

The weight snapshot is taken once for each price reached by one command or reference trade. A later input takes a new snapshot from the resulting state.

For example, an aggressor with quantity 25 first consumes 5 anonymous units at 99 ticks. At 100 ticks it then encounters a 60-unit local order and 40 anonymous units. The new allocatable quantity is 20, so the level allocates 12 local and 8 anonymous. Using the original quantity of 25 again would invent volume; recalculating after each fill would make the result depend on iteration order.

Input sequence determines the snapshot. If an atomic quote update changes the anonymous quantity at 100 from 40 to 60 before a quantity-20 trade, the weights are 60 and 60 and the allocation is 10 and 10. If the quote update follows the trade, the weights remain 60 and 40 and the allocation is 12 and 8.

### Self-trade prevention

The v1 action is cancel incoming. At the first self-owned resting order in deterministic queue order, that order becomes a stop boundary. Earlier anonymous interest may execute; the self-owned order and everything after it are excluded from the pro-rata snapshot. The incoming remainder is then canceled for self-trade prevention, and the resting order is unchanged. External reference trades have no simulated-participant owner and do not trigger STP.

For example, the queue at 100 ticks contains 20 anonymous units, a 30-unit self-owned order, then 50 newer anonymous units. An incoming local order for 40 executes 20 against the first anonymous interest and cancels its remaining 20. It does not execute against the self-owned order or the later anonymous interest. If the self-owned order is first, all 40 incoming units are canceled.

These choices define engine behavior only. Command validation and future allocation results belong in `Shift.Engine`; mapping accepted results to `OrderUpdated` or `TradeExecuted` remains deferred.

## Belongs here

- Orders, price levels, matching rules, and executions.
- LocalOrderBook and ReferenceBook behavior.

## Does not belong here

- Account-wide risk policy or client sessions.
- Feed parsing, journaling, database access, or networking.
