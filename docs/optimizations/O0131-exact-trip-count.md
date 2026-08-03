# O0131 — Exact trip-count computation

| | |
|---|---|
| **Status** | ⬜ Planned as a reusable analysis (the constant case is computed ad hoc by [O0007](O0007-loop-unrolling.md) and [O0020](O0020-idiom-replacement.md)) |
| **Stage** | Mid-end analysis |
| **Related** | [O0007](O0007-loop-unrolling.md), [O0112](O0112-countdown-loop.md), [O0129](O0129-unroll-factor-cost-model.md), [O0016](O0016-value-fact-analysis.md) |

## The idea

Almost every loop transformation needs the same fact: **how many times does this
loop run?** Today two passes compute it independently by simulating the
iterates, and every other pass simply gives up when the count is not a literal.

A single analysis should derive the count symbolically from start, end, step and
comparison semantics — `(to - from + step) \ step` in the simple case, with the
signed/unsigned, wrap and zero-trip cases handled once and correctly.

## Applies to

```basic
FOR i% = a% TO b% STEP 3      ' count = (b% - a% + 3) \ 3, clamped at 0
```

## Today

`TryEmitUnrolledFor` and `TryEmitForIdiom` each simulate the iterates exactly
(signed compare, 16-bit wrap on increment) and bail past 32 767 — correct, but
constant-only and duplicated.

## Planned

A `TripCount` query returning a constant, a symbolic expression, or "unknown",
consumed by unrolling, countdown conversion, versioning, vectorization and the
cost model.

## What it needs

- **PB's exact semantics**, which are the whole difficulty: increment-then-test,
  a 16-bit counter that wraps (QUIRK 2.28), a `STEP` that may be negative or
  (dynamically) zero, and float counters with their own exactness rules.
- The interval domain ([O0016](O0016-value-fact-analysis.md)) for the symbolic
  case, which already reasons about the counter's range.
