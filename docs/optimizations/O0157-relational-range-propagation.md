# O0157 — Relational range propagation

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | SSA mid-end |
| **Related** | [O0016](O0016-value-fact-analysis.md), [O0097](O0097-repeated-comparison-elimination.md), [O0117](O0117-bounds-check-merging.md) |

## The idea

The value lattice tracks each variable **independently**: an interval, its known
bits, its congruence. What it cannot express is a *relation* — `x < y`,
`i <= n`, `p <> q` — even though relations are what most useful proofs need.

The payoff is concrete: `FOR i% = 0 TO n%` with a preceding `IF n% <= UBOUND(a)`
proves every `a(i%)` in range, but only if the analysis can carry `i% <= n%` and
`n% <= UBOUND` at the same time.

## Applies to

```basic
$ERROR BOUNDS ON
DIM a%(0 TO 99), i%, n%
IF n% <= 99 THEN
  FOR i% = 0 TO n%
    a%(i%) = i%              ' provably in bounds — but only via n%
  NEXT
END IF
```

## Today

The counter's range is `[0, n%]` with `n%` unknown, so the bounds check stays.

## Planned

`i% <= n%` (from the loop) plus `n% <= 99` (from the guard) gives
`i% <= 99`, and the check is dropped
([O0016](O0016-value-fact-analysis.md) does the rest).

## What it needs

- A relational domain — pairwise difference constraints (`x - y <= k`, the
  "octagon"/DBM family) are the usual choice, and they are cheap enough for a
  compiler that already runs a per-statement lattice.
- Transfer functions for the branch refinements and for the loop fixpoint, plus
  the same **wrap check** every other domain needs: a relation that holds
  mathematically can fail once an operand wraps.
