# O0111 — Redundant induction-variable elimination

| | |
|---|---|
| **Status** | 🟨 Partial — equal and constant-offset same-step integer induction variables are eliminated; general affine/SCEV equivalence remains planned |
| **Stage** | Mid-end |
| **IR** | ✅ `Ir/Passes/PhiCongruence.cs` — equal loop-carried values are merged, and two same-width integer phis with constant starts plus the same constant wrapping step are represented by one carried phi and one header-local constant offset. In `IrPassManager.Standard()` before GVN; verified by `PhiCongruenceTests`, `OffsetInductionCongruenceTests` and `IrPassObservableEquivalenceTests` |
| **Related** | [O0110](O0110-general-induction-variables.md), [O0112](O0112-countdown-loop.md), [O0027](O0027-copy-propagation.md), [O0016](O0016-value-fact-analysis.md) |

## The idea

When two loop-carried variables advance in lockstep, only one needs to exist:
the other is an affine function of it, or can be dropped entirely. The classic
case is a hand-written index and an offset maintained side by side, which is
exactly how DOS-era BASIC code walks two parallel arrays.

## Applies to

```basic
DIM i%, j%, a%(0 TO 99), b%(0 TO 99)
j% = 100
FOR i% = 0 TO 99
  a%(i%) = b%(j% - 100)      ' j% - 100 is always i%
  j% = j% + 1
NEXT
```

## Implemented slice

For two phis of the same integer type,

```text
i0 = C0                    j0 = D0
i' = i + step              j' = j + step
```

with the same two predecessor edges and the same constant `step`, the difference
is invariant in the type's bit domain:

```text
j[n] = i[n] + (D0 - C0)  (mod 2^width)
```

`PhiCongruence` therefore replaces the second carried phi with one header-local
`i + delta`. Its recurrence update becomes dead and the ordinary value passes can
cancel compensating offsets such as `(j - 100)` back to `i`.

This is exact even when the recurrence wraps. The IR's ordinary integer `add`
has wrapping fixed-width semantics, and adding the same step to both sides
preserves their modular difference. A no-wrap range proof is therefore *not*
required for this specific same-width/same-step relation.

The derived value is defined in the loop header. It consequently dominates
ordinary body and exit uses, so PB's observable post-loop value remains available
without a special exit materialization.

## Result

Conceptually the example becomes:

```basic
FOR i% = 0 TO 99
  a%(i%) = b%(i%)
NEXT
j% = i% + 100                ' represented from the surviving IV when needed
```

The second loop-carried update no longer consumes a register each iteration.

## Conservative boundary

The current implementation deliberately refuses cases that need more loop/SSA
infrastructure:

- different steps, non-constant starts or non-canonical recurrence expressions;
- different predecessor/latch structure or different integer widths;
- uses of the candidate IV as an incoming value of another phi, because moving a
  header-defined derived value onto a predecessor edge needs an LCSSA/dominance
  rewrite rather than a local substitution;
- general relations such as `j = base + i * stride`, multiple derived IV levels,
  pointer IVs, and cross-width recurrences.

Those broader cases belong to the shared affine-IV/SCEV-style analysis planned
for [O0110](O0110-general-induction-variables.md).

## Why it is safe

The original equal-phi part uses optimistic congruence classes: loop phis start
congruent and split whenever an incoming edge proves otherwise, which handles
cyclic equality that pessimistic GVN cannot establish.

For the offset slice, fixed-width modular arithmetic is the proof. If
`j = i + delta (mod 2^n)` and both are advanced by the same `step`, then
`j + step = i + step + delta (mod 2^n)`. The invariant therefore holds for every
iteration including wraparound. The transform changes representation only; it
does not alter the loop's branch, trip count, memory operations or side effects.
