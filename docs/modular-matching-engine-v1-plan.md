# Prerequisite-First Modular Simulator Matching Engine v1

## Goal

Build a deterministic, single-instrument, single-writer `MatchingEngine` in `Shift.Engine`. It is a policy-driven continuous matching kernel, not a hard-coded price-time book and not a Nasdaq compatibility claim. [OUCH 5.0](https://www.nasdaqtrader.com/content/technicalsupport/specifications/TradingProducts/OUCH5.0.pdf) defines order entry, [ITCH 5.0](https://www.nasdaqtrader.com/content/technicalsupport/specifications/dataproducts/NQTVITCHSpecification_5.0.pdf) publishes market-data lifecycle, and [Nasdaq Equity Rule 4757](https://listingcenter.nasdaq.com/rulebook/nasdaq/rules/Nasdaq%20Equity%204) documents one matching-priority model.

`MatchingEngine` owns state and invariants. An immutable `MatchingProfile` selects a complete bundle of narrowly typed, stateless hooks for every in-scope microstructure decision. The profile is fixed for the engine's lifetime. The same ordered inputs, build, and profile ID/version must produce the same results and final state.

Before matching logic:

1. Document the fixed kernel invariants, hook call order, supported instruction matrix, and first two profiles in the [matching README](/Users/marcus/repos/school/shift-cs/src/Shift.Engine/Matching/README.md:1).
2. Replace the exception-throwing public [LocalOrderBook](/Users/marcus/repos/school/shift-cs/src/Shift.Engine/Matching/LocalOrderBook.cs:3) with a public typed `MatchingEngine`; make the low-level books internal and policy-neutral.
3. Give each named profile one stable ID and version. Per-hook IDs, a manifest format, and wire-protocol changes are outside this slice.
4. Establish these caller contracts:
   - One engine instance represents one instrument, one profile, and one trading day.
   - The caller serializes operations; the engine has no locks.
   - Instrument ID, sequence, timestamps, tick validation, price bands, risk, latency, fees, persistence, and publication remain outside.
   - The reference adapter supplies atomic BBO snapshots and trades in deterministic order.
5. Specify a second real same-price policy before freezing the priority and allocation hooks. Pro-rata is the intended proof, but its weights, rounding, snapshot timing, minimum allocation, anonymous-reference participation, and self-trade interaction must be decided first.
6. Do not change frame codecs, protocol messages, sequencer, persistence, networking, gateways, risk, or public market-data publication.

The committed baseline at `5d6a5d5` has 111 repository tests. Preserve any later protocol work already in progress.

## Ownership

- Public `MatchingEngine` owns lifecycle, validation, canonical state mutation, hook coordination, result construction, and invariant checks.
- Public immutable `MatchingProfile` is configuration only. It identifies one complete, compatible hook bundle.
- Internal `MatchingHooks` holds the stateless hook implementations selected by the profile.
- Internal `OrderBook` owns participant-order facts and lookup.
- Internal `ReferenceBook` owns the latest raw BBO only.
- Internal `ReferenceLiquidityState` owns scenario-specific anonymous capacity, queue positions, and consumption credits.

Hooks receive read-only typed views and return small typed decisions. They never receive mutable books or caller-owned output lists. Only `MatchingEngine` changes orders, reference state, modeled liquidity, fills, cancellations, or output buffers.

Hooks are trusted engine code in v1. The engine validates each hook result before applying it, and invalid output is an internal invariant failure. Do not add a general transaction, rollback layer, service locator, dependency-injection container, reflection, event bus, or untyped `BeforeMatch`/`AfterMatch` callbacks.

Keep hook interfaces internal and expose named profiles publicly. This keeps the low-level book view out of the public API. Promote external hook implementation to a public API only when another assembly actually needs to author a rule.

## Fixed kernel invariants

Hooks cannot change:

- One instrument, one single writer, and one immutable profile.
- Lifecycle `NotStarted -> Open -> Ended`. An ended engine cannot be started again.
- Positive order IDs, prices, and quantities; checked arithmetic; and live-order ID uniqueness.
- Fixed validation order: output buffer and input shape, stateless request-policy validation, lifecycle, then state-dependent checks.
- Every caller rejection occurs before the first mutation and leaves all state and output lists unchanged.
- No negative quantity, overfill, duplicate consumption, or invented liquidity.
- For every accepted placement, `filled + resting + canceled == requested`.
- For every accepted reference trade, `allocated + unallocated == reported`.
- Caller-owned result lists are reusable and append-only. The engine alone appends to them.
- Fill coalescing once per participant order and price within one operation.
- One implicit simulated owner. Any incoming local order that encounters a resting local order is a self-match candidate.
- Deterministic day-end cancellation order: buys then sells, price levels in the active `IPricePriorityHook` order, and accepted-arrival order within each level.
- Raw reference observations remain separate from scenario-specific modeled consumption.
- No I/O, wall-clock reads, ambient randomness, culture-sensitive behavior, mutable global state, or unordered collection iteration in the engine or hooks.

The engine assigns:

- immutable `AcceptedArrivalOrdinal` to each accepted local order for audit and day-end order;
- current `PriorityOrdinal` whenever local or anonymous interest first rests or loses priority;
- a stable internal `InterestId` so every ordering hook has a total deterministic fallback.

Tick grids, lot sizes, price bands, and account rules remain caller-owned. Basic positive-number and enum-shape checks remain kernel validation.

## Hook bundle

Each hook owns one decision. No hook invokes another hook.

| Hook | Decision owned by the hook | `PriceTimeBboShadowV1` behavior |
|---|---|---|
| `IOrderAdmissionHook` | Map a structurally valid public `OrderType` to internal time-in-force and liquidity semantics, or reject a known type for the active venue | Accept Day, IOC, and post-only Day limits |
| `IPostOnlyHook` | Which pre-self-trade encounters block posting; returns proceed or accepted cancellation | Cancel when executable modeled-reference or local contra interest would be encountered |
| `ICrossingHook` | Price compatibility only | A buy crosses prices at or below its limit; a sell crosses prices at or above it |
| `IPricePriorityHook` | Ordering among already compatible price levels | Best price first |
| `IQueuePositionHook` | Proposed position when local or anonymous interest joins or requeues, plus which anonymous capacity a quote-only partial shrink removes | Local arrivals and same-price reference increases join behind; returning BBO enters ahead; partial shrink removes newest reference capacity first |
| `ILiquidityPriorityHook` | Ranking of local versus anonymous-reference interest at one price, using stored queue position but not time | Follow modeled queue position |
| `ITimePriorityHook` | Ordering inside one already selected liquidity tier | Lowest current `PriorityOrdinal` first |
| `ISelfTradePreventionHook` | Action and stop boundary when ranked interest contains a local contra order | Preserve earlier anonymous allocations, then cancel the incoming remainder at the first local contra |
| `IAllocationHook` | Quantities within a pre-ranked, self-trade-filtered tier, including deterministic rounding | Assign remaining quantity sequentially in supplied priority order |
| `IExecutionTermsHook` | Execution price, participant fill legs, and maker/taker role | Resting price; a local taker against anonymous capacity emits its taker fill; a local passive fill is maker; a reference trade consuming only anonymous capacity emits no participant fill |
| `ITimeInForceHook` | Whether an accepted unmatched remainder rests or cancels and its cancellation reason | Day rests; IOC cancels |
| `IReductionHook` | Whether a validated partial reduction that leaves quantity remaining retains or resets priority | Retain priority |
| `IReferenceQuoteHook` | Raw snapshot acceptance and raw-side transitions, including whether a local/quote cross is accepted | Accept empty/one-sided BBO, reject raw locked/crossed BBO, allow local/quote crosses, and retire a raw side on move/removal |
| `IReferenceCreditHook` | Credit creation, capping, reconciliation, and retirement | Credit anonymous consumption at the raw price, cap at raw size, apply credit before same-price shrink, and reset on move/removal |
| `IReferenceTradeClassifierHook` | Aggressor-side inference and ambiguity | Infer from combined executable best prices; both or neither is ambiguous |
| `IReferenceTradeScopeHook` | The synthetic aggressor boundary for an accepted observed trade | Stop at the reported trade price |

The four same-price hooks do not overlap:

- `IQueuePositionHook` returns decisions only when interest joins or requeues, or a quote-only partial shrink must choose anonymous capacity; it never writes state or selects executable interest. Depletion, cancel, reduce-to-zero, and full side retirement remove entries mechanically in the engine.
- `ILiquidityPriorityHook` ranks liquidity classes and positions; it ignores temporal order inside an equal tier.
- `ITimePriorityHook` orders interests inside an equal tier from existing priority metadata; it never inserts or mutates an order.
- `IAllocationHook` receives the resulting ranked interests and assigns quantities only.

`IReferenceQuoteHook` validates raw observations; it does not place capacity, calculate credit, or allocate trades. `IReferenceTradeScopeHook` supplies a boundary; `ICrossingHook`, `IPricePriorityHook`, the same-price hooks, and `IAllocationHook` remain the sole owners of executable-level selection and quantity allocation. Returning unallocated reported quantity remains a fixed kernel invariant.

Quote updates cannot fill, cancel, or reprice participant orders in v1 because `UpdateReferenceQuote` has no execution or cancellation outputs. The reference-quote hook may accept or reject a local/quote cross, but an accepted update only changes raw and modeled reference state.

## Public contract

Construct the engine with one explicit named profile:

- `MatchingEngine(MatchingProfile profile)`
- `MatchingProfiles.PriceTimeBboShadowV1`
- `MatchingProfiles.ProRataBboShadowV1` after its prerequisite decisions are complete

Keep the existing public `OrderType` and wire representation. `IOrderAdmissionHook` immediately normalizes each accepted value into independent internal semantics so the matching path does not switch on the combined enum:

| Public order type | Internal time in force | Internal liquidity constraint |
|---|---|---|
| `DayLimit` | Day | None |
| `ImmediateOrCancelLimit` | Immediate or cancel | None |
| `PostOnlyLimit` | Day | Post only |

This keeps the current protocol stable while separating the actual hook decisions. Do not add hidden, reserve, market, FOK, timed, stop, peg, routed, auction, or replace values until one of those behaviors is implemented. Unknown enum values receive a typed rejection.

Other public value types:

- Existing `OrderSide`: `Buy`, `Sell`.
- `ReferenceLevel`: positive `PriceTicks` and `Quantity`.
- `ReferenceLevelState`: price, raw observed quantity, and executable modeled quantity.
- `Fill`: participant order ID, price ticks, quantity, and `Maker`/`Taker` role.
- `CanceledOrder`: order ID and canceled quantity.
- One shared `RejectionReason` enum and one `CancellationReason` enum, with explicit numeric values.
- `MatchingProfileId` and profile version.

Public operations:

- `StartDay()`
- `EndDay(List<CanceledOrder>)`
- `PlaceOrder(PlaceOrder command, List<Fill>)`, reusing the protocol command record
- `CancelOrder(CancelOrder command)`, reusing the protocol command record
- `ReduceOrder(orderId, reductionQuantity)`
- `UpdateReferenceQuote(optionalBid, optionalAsk)`
- `RecordReferenceTrade(priceTicks, quantity, List<Fill>)`
- Queries for lifecycle, profile ID/version, live-order count, order lookup, best local price by side, canonical price-level snapshots, and observed/available reference BBO.

Do not expose `TryGetBestLocalOrder`: FIFO has one obvious head, while pro-rata can make several orders simultaneously eligible. Queries expose facts; hooks decide execution priority.

Use method-specific result structs:

- Placement: rejection, cancellation reason, filled/resting/canceled quantities, and appended fill start/count.
- Cancel: rejection and canceled quantity.
- Reduce: rejection, resulting remaining quantity, and whether priority reset.
- Quote update: rejection.
- Reference trade: rejection, interpreted aggressor side, unallocated quantity, and appended fill start/count.
- Day lifecycle: rejection; `EndDay` also returns appended cancellation start/count.

All output lists are caller-owned, reusable, and append-only. Unknown enum values and unsupported order types are input-policy rejections before lifecycle checks. Only corrupted internal invariants, invalid internal hook output, or unrecoverable runtime failures may throw.

Reuse the existing lifecycle rejection values without changing the protocol: `StartDay` from either `Open` or `Ended` returns `DayAlreadyStarted`; mutations and `EndDay` outside `Open` return `DayNotStarted`. In this engine contract, `DayNotStarted` therefore means the day is not open.

Retain typed reasons for invalid output buffer, order ID, side, price, quantity, order type, reference quote, arithmetic overflow, lifecycle, duplicate ID, unknown order, over-reduction, raw locked/crossed quote, and ambiguous reference trade. `IOrderAdmissionHook` uses `UnsupportedOrderType`; a profile-specific quote rejection that is not a raw lock/cross uses `InvalidReferenceQuote`.

## Hook call order

### Place order

1. Validate output buffer and primitive input shape, including whether the public enum value is defined.
2. Call `IOrderAdmissionHook` to normalize or reject the public `OrderType`.
3. Validate lifecycle, duplicate ID, and other state-dependent conditions.
4. For post-only, build a nonmutating encounter summary from the existing state using crossing, price priority, liquidity priority, time priority, and allocation. Do not apply self-trade prevention: the first local contra still blocks the default post-only order. `IPostOnlyHook` returns proceed or accepted cancellation.
5. Use `ICrossingHook` to identify compatible contra levels and `IPricePriorityHook` to choose the next one.
6. Use `ILiquidityPriorityHook` and `ITimePriorityHook` to produce ranked interests at that price.
7. Call `ISelfTradePreventionHook` before allocation. It marks an action and, when required, a stop boundary in the ranked interests.
8. Pass only the executable prefix or tier to `IAllocationHook`.
9. Call `IExecutionTermsHook` for each proposed allocation and `IReferenceCreditHook` for each anonymous-reference consumption.
10. Validate the level decision: every interest is live and eligible, quantities are positive and available, total allocation does not exceed incoming quantity, execution prices satisfy the crossing/limit boundary, participant legs and roles are valid, and credit remains within the raw side quantity.
11. Apply the level decision. If self-trade prevention terminates the order after earlier allocations, cancel the incoming remainder; otherwise repeat steps 5-11 while quantity and compatible liquidity remain.
12. If normal unmatched quantity remains, call `ITimeInForceHook`.
13. If it rests, call `IQueuePositionHook`, assign a new `PriorityOrdinal`, and insert it. Otherwise record its cancellation reason.
14. Verify conservation, coalesce fills, and append results.

All caller-rejecting checks occur before step 5. Matching hooks cannot return caller rejections after mutation begins. Invalid internal hook output is an invariant failure; do not build a general rollback system for engine bugs.

### Other operations

- Cancel: input shape -> lifecycle -> lookup -> apply removal.
- Reduce: input shape -> lifecycle -> lookup and quantity bounds -> remove at zero, otherwise `IReductionHook` -> if priority resets, call `IQueuePositionHook` and assign a new `PriorityOrdinal` -> apply.
- Quote update: primitive shape -> `IReferenceQuoteHook.ValidateSnapshot` -> lifecycle -> `IReferenceQuoteHook.PlanTransition` -> `IReferenceCreditHook` -> `IQueuePositionHook` -> validate the complete bid/ask plan -> apply atomically.
- Reference trade: primitive shape -> lifecycle -> `IReferenceTradeClassifierHook` -> `IReferenceTradeScopeHook` -> ordinary crossing/price/liquidity/time/allocation pipeline -> `IExecutionTermsHook` -> `IReferenceCreditHook` -> apply -> return diagnostic remainder.
- Day start: reject unless `NotStarted`, clear initial state, then open.
- Day end: reject unless `Open`, append cancellations in the fixed kernel order, clear local/reference/modeled state, then enter `Ended`.

The reference-trade path reuses the ordinary priority and allocation pipeline. It does not contain a second hard-coded matcher.

## First profile: `PriceTimeBboShadowV1`

These are profile choices, not kernel behavior.

Order handling:

- Day limit orders sweep compatible prices and rest any residual.
- IOC limit orders use the same matching path and cancel any residual.
- Post-only Day limits cancel in full if their pre-self-trade encounter summary contains executable modeled-reference or local contra interest.
- Executions use the resting price.
- Self-match prevention preserves earlier anonymous allocations, then cancels the incoming remainder at the first local contra.
- Full cancel removes a live order.
- Partial reduction preserves priority; reduction to zero removes the order.
- Order IDs may be reused after the prior order is no longer live.

Reference quote behavior:

- A quote update supplies optional bid and ask together; both absent is a valid empty book.
- Nonpositive level values or raw bid greater than or equal to raw ask reject the entire snapshot.
- Quote changes never generate fills and may leave reference liquidity crossed through a local order.
- Same-price increases join behind existing queue entries.
- Same-price decreases consume credit first, then anonymous modeled capacity newest-first.
- A new or returning BBO price places its full quantity ahead of local orders at that price.
- A raw price change or side removal retires old modeled capacity and credit; it creates no retained off-BBO depth.
- Consumption credit is capped at raw side quantity and records anonymous capacity consumed by simulated takers or accepted reference-trade volume at that raw price.
- Post-only checks executable modeled state, not exhausted raw quantity.

Reference trade behavior:

1. Infer aggressor side from combined executable local and modeled-reference best prices.
2. Buy is valid when price reaches the combined ask and not the combined bid; sell is the inverse. Both or neither is ambiguous.
3. Exclude raw quotes whose modeled executable capacity is exhausted.
4. Permit inference from local orders without a raw side, but record no quote credit in that case.
5. Sweep compatible prices through the reported trade price and consume observed quantity once.
6. Fill local passive orders at their resting prices as makers; consuming anonymous capacity creates no participant fill.
7. Return quantity that cannot be allocated; never invent depth or reject the otherwise valid observation.

## Policy-neutral state design

Retain the current useful mechanics without retaining their embedded policy:

- One live-order dictionary for average O(1) lookup.
- Side-specific ordered price maps constructed with the total comparers supplied by `IPricePriorityHook`, with the first level cached. The book contains no hard-coded bid/ask comparer.
- A stable intrusive accepted-arrival list at each price for O(1) local unlink and deterministic canonical enumeration. Its head is not automatically the next executable order.
- `AcceptedArrivalOrdinal`, mutable `PriorityOrdinal`, and stable `InterestId` as separate facts.
- O(log price-level count) insertion/removal and work proportional to entries inspected by the active hooks.

`ReferenceLiquidityState` stores lightweight anonymous capacity slices and their positions relative to local interests. A slice is not a participant order and has no public order ID. The queue hook chooses insertion and quote-shrink targets; the engine applies them. The ordinary same-price hooks receive one transient read-only view combining local orders and anonymous slices.

Do not place `ExternalQuantityAhead` or `ExternalQuantityBehind` on a generic local-order node. Those fields encode FIFO queue-ahead behavior. Adjacent anonymous slices may be coalesced only when doing so preserves every active hook decision.

For the hot path, use read-only structs and nonallocating iteration for hook contexts. Invoke hooks at decision boundaries, not once per quantity unit. Do not add pooling, fixed capacities, or specialized collections until benchmarks show a specific need.

## Tests and benchmarks

Kernel contract tests run against both complete profiles:

- lifecycle, validation order, checked arithmetic, ID reuse, cancel, reduce, and no mutation on caller rejection;
- quantity conservation and no duplicate consumption;
- caller-owned append-only outputs and fill coalescing;
- exact deterministic day-end cancellation order and clearing;
- deterministic replay including profile ID/version;
- hook-result invariant checks.

Each hook receives focused table-driven tests for every branch and deterministic tie. `PriceTimeBboShadowV1` integration coverage includes:

- full, partial, nonmarketable, self-cross, and residual handling for every accepted order type;
- price ordering, time priority, multi-level sweeps, resting-price fills, and fill coalescing;
- post-only against anonymous capacity, exhausted raw capacity, and local contra orders;
- atomic empty/one-sided BBO updates, invalid snapshots, increases, decreases, reentry, price moves, and local/quote crosses;
- trade classification, ambiguity, local-only inference, passive fills, credit reconciliation, and unallocated volume;
- checked-arithmetic boundaries and null output buffers.

Before freezing the hook API, run one identical scenario through price-time and pro-rata profiles and prove that only same-price allocation changes. The pro-rata specification must define eligible weight, allocation snapshot timing, rounding in integer quantity units, residual distribution, minimum allocation, anonymous-reference participation, and whether self-owned interest is removed before the allocation snapshot or terminates the aggressor.

After focused deterministic tests pass, add the deterministic randomized comparison against an independent slow model. The slow model must read an explicit profile choice rather than assume FIFO.

Keep recorded, non-gating benchmarks for resting at existing/new prices, cancel, reduce, IOC external fills, single- and multi-price sweeps, passive maker fills, quote reconciliation, post-only dry runs, and day-end clearing. Compare the two real profiles. Measure hook dispatch separately only if an end-to-end benchmark identifies it as material. Do not add hard CI latency SLAs, pooling, or fixed capacities yet.

## Implementation order

1. Put the fixed-invariant/hook table, call order, and exact price-time profile matrix in the matching README.
2. Resolve the pro-rata prerequisites, including its self-trade interaction, with numeric examples, then add its exact profile matrix.
3. Reuse the existing protocol order, fill, and rejection value types. Define the method-specific engine results, profile ID/version, internal normalized order semantics, hook contexts, and hook decisions.
4. Define the internal stateless hook interfaces and explicit profile-to-hook composition. There are no optional or implicit hooks.
5. Refactor `LocalOrderBook` into policy-neutral internal state while preserving its dictionary and unlink mechanics. In the same change, move low-level tests behind `InternalsVisibleTo` if still useful and move benchmarks to the public engine.
6. Implement non-reference placement, cancellation, and reduction through the price-time hooks.
7. Implement the pro-rata priority/allocation hooks and run the shared kernel suite. Remove any hidden FIFO assumptions before adding reference complexity.
8. Implement `ReferenceBook`, `ReferenceLiquidityState`, and the reference hooks without synthetic participant orders.
9. Add post-only encounter scanning and reference-trade matching by reusing the ordinary hook pipeline.
10. Add replay-equivalence tests, the independent slow model, and end-to-end benchmarks.
11. Update the matching README from planned to implemented behavior only after each slice passes.

Keep related hook interfaces and their small context/decision types together initially. Keep each named profile's implementations together initially. Split files only when their implemented code becomes hard to scan.

## Deliberate exclusions

The v1 hook surface covers continuous displayed-limit matching and the top-of-book shadow model. It does not predeclare hooks for hidden/reserve priority, auctions, halts, routing, pegs, midpoint books, stops, FOK/minimum quantity, multi-instrument atomicity, or randomized speed bumps. Add a typed hook only with implemented behavior that needs it.

Latency/local-view delay schedules commands before this engine. Fees and rebates consume immutable fills after this engine. Risk gates commands before matching and reconciles accepted results afterward. Keeping those policies adjacent but outside prevents them from mutating price-level state or changing deterministic hook order.
