# O0347 — Mixed-precision computation

| | |
|---|---|
| **Status** | 🟨 Partial — one exact binary32 multiply narrowing is implemented; general error-budget analysis remains planned |
| **Stage** | IR middle end |
| **Gate** | Ordinary optimizer for the exact case |
| **IR** | `FpSimplify.NarrowDemandedPrecision` |
| **Related** | [O0012](O0012-float-demotion.md), [O0057](O0057-storage-narrowing.md), [O0346](O0346-fp-classification-simplification.md) |

## What is implemented

The middle end recognizes this exact shape:

```text
fptrunc (fmul (fpext a:f32 to f64), (fpext b:f32 to f64)) to f32
```

when both source operands are proven finite/non-NaN. It replaces the sequence
with an `fmul f32` directly.

This is not an approximation. Two binary32 significands need at most 48 bits
for an exact product, while binary64 has 53 significand bits, so the widened
multiply introduces no intermediate rounding before the final binary32 round.
The direct binary32 multiply therefore has the same final result for the proven
finite case.

Because it is exact, this subset belongs in ordinary optimization and does not
require SPEED.

## Deliberate boundary

Addition/subtraction are not generalized by analogy: widened addition followed
by narrowing can encounter double-rounding cases. Accumulators, chained
operations and arbitrary `DOUBLE -> SINGLE` demotion need a real propagated
error budget and are not claimed by this implementation.

The broader "this value is really an integer" case remains
[O0012](O0012-float-demotion.md).
