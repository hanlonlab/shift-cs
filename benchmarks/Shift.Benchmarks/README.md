# Shift.Benchmarks

This project measures hot paths such as frame coding, sequencing, log append, book updates, risk checks, and matching.

The committed-message benchmark covers submission ingress, Sequencer batching, the direct Archiver call and durable flush, and committed multicast delivery within the combined process.

## Belongs here

- Small benchmarks with explicit inputs and allocation or latency results.

## Does not belong here

- Feature tests, deployment load tests, or speculative micro-optimizations.
