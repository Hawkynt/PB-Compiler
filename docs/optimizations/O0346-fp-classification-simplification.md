# O0346 — Floating-point classification simplification

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0016](O0016-value-fact-analysis.md), [O0012](O0012-float-demotion.md), [C0003](C0003-x87-scheduling.md) |

## The idea

Float code carries defensive cases — NaN checks, sign tests, zero comparisons —
that value facts can often decide. Where a value is provably **finite,
non-negative or non-NaN**, the corresponding branch and its `FSTSW`/`SAHF` round
trip disappear.

The x87 comparison protocol is what makes this worth doing: every float compare
costs a status-word store and a flag transfer, so removing one removes five
instructions, not one.

## Applies to

```basic
DIM x!, y!
x! = a! * a!                 ' provably non-negative
IF x! >= 0 THEN y! = SQR(x!) ' the test is always true
```

## What it needs

- A **float fact domain** alongside the integer lattice
  ([O0016](O0016-value-fact-analysis.md)): sign, finiteness, NaN-freedom,
  exact-integer-ness — the last of which
  [O0012](O0012-float-demotion.md) already reasons about informally.
- Care with the x87's 80-bit intermediates: a value that is finite in a register
  can overflow when stored, so the facts must be tracked per *storage width*,
  not per value.
