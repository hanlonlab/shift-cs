# Shift.Benchmarks

This project measures hot paths such as frame coding, sequencing, log append, book updates, risk checks, and matching.

The committed-message benchmark covers submission ingress, Sequencer batching, the direct Archiver call and durable flush, and committed multicast delivery within the combined process.

Matching-engine measurements already live in [Shift.ReplayBenchmarks](../Shift.ReplayBenchmarks/README.md): complete recorded-session replay and place-then-fill sweeps of 10, 1,000, and 10,000 orders, with timing and allocation results. That project runs separately from this BenchmarkDotNet suite and requires the recorded sample.

## Belongs here

- Small benchmarks with explicit inputs and allocation or latency results.

## Does not belong here

- Feature tests, deployment load tests, or speculative micro-optimizations.
