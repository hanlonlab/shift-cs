"""Same-input, data-only benchmarks; deliberately makes no fill-equivalence claim."""

import argparse
import csv
from decimal import Decimal
import gc
import hashlib
import importlib.metadata
import json
import platform
import statistics
import time
import warnings
from pathlib import Path

SCALE = 1_000_000


def measure(name, action, logical_events, encoded_events, runs_per_batch):
    action()  # Includes first-use/JIT costs, excluded from timing.
    warmup_end = time.perf_counter() + 1
    while time.perf_counter() < warmup_end:
        action()
    gc.collect()
    samples = []
    for _ in range(30):
        started = time.perf_counter_ns()
        for _ in range(runs_per_batch):
            action()
        samples.append((time.perf_counter_ns() - started) / runs_per_batch / 1_000_000)
    median = statistics.median(samples)
    return {
        "name": name,
        "logical_events": logical_events,
        "encoded_events": encoded_events,
        "runs_per_batch": runs_per_batch,
        "median_milliseconds": median,
        "p95_batch_milliseconds_per_run": sorted(samples)[28],
        "logical_events_per_second": logical_events / (median / 1000),
        "batch_milliseconds_per_run": samples,
    }


def prepare_hft(rows):
    import hftbacktest as hft
    import numpy as np
    from numba import njit

    encoded = []
    flags = hft.EXCH_EVENT | hft.LOCAL_EVENT
    for kind, ts, price, qty, ask, ask_qty, side in rows:
        if kind == "Q":
            # The L2 processor in 2.4.4 does not handle DEPTH_BBO_EVENT.
            # Replace its depth with the two observed levels; no stale depth is invented.
            encoded.extend([
                (flags | hft.DEPTH_CLEAR_EVENT, ts, ts, 0., 0., 0, 0, 0.),
                (flags | hft.BUY_EVENT | hft.DEPTH_EVENT, ts, ts, price / SCALE, qty / SCALE, 0, 0, 0.),
                (flags | hft.SELL_EVENT | hft.DEPTH_EVENT, ts, ts, ask / SCALE, ask_qty / SCALE, 0, 0, 0.),
            ])
        else:
            direction = hft.BUY_EVENT if side == 1 else hft.SELL_EVENT
            encoded.append((flags | direction | hft.TRADE_EVENT, ts, ts, price / SCALE, qty / SCALE, 0, 0, 0.))
    data = np.array(encoded, dtype=hft.event_dtype)
    final_quote = next(row for row in reversed(rows) if row[0] == "Q")
    duration = rows[-1][1] - rows[0][1] + 1_000_000_000

    @njit
    def replay(engine):
        status = engine.elapse(duration)
        depth = engine.depth(0)
        return status, depth.best_bid, depth.best_ask

    def run():
        asset = (hft.BacktestAsset().data(data).linear_asset(1.0)
                 .constant_order_latency(0, 0).risk_adverse_queue_model()
                 .partial_fill_exchange().trading_value_fee_model(0, 0)
                 .tick_size(1 / SCALE).lot_size(1 / SCALE))
        engine = hft.HashMapMarketDepthBacktest([asset])
        try:
            status, bid, ask = replay(engine)
            timestamp = engine.feed_latency(0)[0]
            assert status == 1, status
            assert round(bid * SCALE) == final_quote[2], (bid, final_quote)
            assert round(ask * SCALE) == final_quote[4], (ask, final_quote)
            assert timestamp >= rows[-1][1], timestamp
        finally:
            assert engine.close() == 0

    return run, len(data)


def prepare_nautilus(rows):
    from nautilus_trader.backtest.engine import BacktestEngine
    from nautilus_trader.config import BacktestEngineConfig, LoggingConfig
    from nautilus_trader.model.currencies import USD
    from nautilus_trader.model.data import QuoteTick, TradeTick
    from nautilus_trader.model.enums import AccountType, AggressorSide, AssetClass, BookType, InstrumentClass, OmsType
    from nautilus_trader.model.identifiers import InstrumentId, Symbol, TradeId, Venue
    from nautilus_trader.model.instruments import Instrument
    from nautilus_trader.model.objects import Money, Price, Quantity

    warnings.filterwarnings("ignore", message="Timestamp.utcnow is deprecated.*")

    venue = Venue("REPLAY")
    instrument_id = InstrumentId(Symbol("A"), venue)
    # Equity fixes size precision to zero; use the base instrument to retain fractional shares.
    instrument = Instrument(instrument_id, Symbol("A"), AssetClass.EQUITY, InstrumentClass.SPOT,
                            USD, False, 6, 6, Quantity.from_str("0.000001"), Quantity.from_int(1),
                            Decimal(0), Decimal(0), Decimal(0), Decimal(0), 0, 0,
                            price_increment=Price.from_str("0.000001"), lot_size=Quantity.from_int(1))
    data = []
    for index, (kind, ts, price, qty, ask, ask_qty, side) in enumerate(rows):
        if kind == "Q":
            data.append(QuoteTick(instrument_id, Price(price / SCALE, 6), Price(ask / SCALE, 6),
                                  Quantity(qty / SCALE, 6), Quantity(ask_qty / SCALE, 6), ts, ts))
        else:
            direction = AggressorSide.BUYER if side == 1 else AggressorSide.SELLER
            data.append(TradeTick(instrument_id, Price(price / SCALE, 6), Quantity(qty / SCALE, 6),
                                  direction, TradeId(str(index)), ts, ts))
    final_quote = next(item for item in reversed(data) if isinstance(item, QuoteTick))
    config = BacktestEngineConfig(logging=LoggingConfig(bypass_logging=True), run_analysis=False)

    def run():
        engine = BacktestEngine(config=config)
        try:
            engine.add_venue(venue, OmsType.NETTING, AccountType.CASH, [Money(1_000_000, USD)],
                             book_type=BookType.L1_MBP, trade_execution=True,
                             liquidity_consumption=True, queue_position=True)
            engine.add_instrument(instrument)
            engine.add_data(data)
            engine.run()
            assert engine.iteration == len(data), engine.iteration
            assert engine.cache.quote_tick(instrument_id) == final_quote
        finally:
            engine.dispose()

    return run, len(data)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("tape", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--engine", choices=["hftbacktest", "nautilus"], required=True)
    parser.add_argument("--repeat-days", type=int, default=1)
    args = parser.parse_args()
    if args.repeat_days < 1:
        parser.error("--repeat-days must be positive")
    with args.tape.open(newline="") as source:
        reader = csv.reader(source)
        next(reader)
        original = [(row[0], *map(int, row[1:])) for row in reader]
    rows = [(kind, ts + day * 86_400_000_000_000, price, qty, ask, ask_qty, side)
            for day in range(args.repeat_days)
            for kind, ts, price, qty, ask, ask_qty, side in original]
    prepare = prepare_hft if args.engine == "hftbacktest" else prepare_nautilus
    action, encoded_events = prepare(rows)
    report = {
        "python": platform.python_version(),
        "machine": platform.machine(),
        "version": importlib.metadata.version("hftbacktest" if args.engine == "hftbacktest" else "nautilus_trader"),
        "repeat_days": args.repeat_days,
        "source_sha256": hashlib.sha256(args.tape.read_bytes()).hexdigest(),
        "measurement": measure(args.engine, action, len(rows), encoded_events,
                               5 if args.repeat_days == 1 else 1),
    }
    args.output.write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
