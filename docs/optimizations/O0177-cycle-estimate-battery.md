# O0177 — Cycle-estimate assertions in the optimization battery

| | |
|---|---|
| **Status** | ⬜ Planned (test infrastructure, not a compiler pass) |
| **Stage** | Test architecture |
| **Related** | [O0174](O0174-target-cost-models.md), [O0129](O0129-unroll-factor-cost-model.md), [O0147](O0147-vector-width-cost-model.md) |

## Why it belongs in this list

Every planned optimization above is judged by a battery that today asserts
**instruction patterns and sizes**. On an 8086 that measure lies:

- `IMUL` (~120 cycles) against a three-instruction shift/add chain (~10) — the
  chain is *larger* and far faster ([O0078](O0078-multiply-decomposition.md));
- a two-byte `JMP` that is *taken* flushes the prefetch queue and costs more
  than several bytes of fall-through code;
- a memory operand folded into an ALU op removes an instruction but not a bus
  cycle.

So "smaller than unoptimized" is the wrong assertion for half of the planned
work, and passing it would mean rejecting correct optimizations.

## What it needs

A per-target scenario configuration and richer assertions:

```text
8086-speed   386-speed   pentium-speed   p6-speed
x86-64-sse2  x86-64-avx2 x86-64-avx512   generic-size
```

```basic
' @target   x86-64-avx2
' @require  vector-width 256
' @assert   present vpaddd
' @assert   vectorized-loop
' @assert   no-scalar-main-loop
' @assert   max-loads-per-iteration 0.25
' @assert   estimated-throughput-better-than-unoptimized
```

- an **estimated-cycle** model driven by the same tables as
  [O0174](O0174-target-cost-models.md) — the estimator and the optimizer's cost
  model must be the same data, or the tests validate a different compiler than
  the one that ships;
- the existing **byte-identical differential oracle** stays exactly as it is:
  cycle estimates judge quality, the oracle judges correctness, and neither
  substitutes for the other.
