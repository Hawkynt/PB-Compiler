# O0126 — Unroll and jam

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0007](O0007-loop-unrolling.md), [O0120](O0120-multiple-accumulators.md), [O0127](O0127-loop-interleaving.md) |

## The idea

Unroll the **outer** loop of a nest and fuse ("jam") the resulting copies of the
inner loop into one. Two things follow: values loaded for the outer iterations
are reused inside the merged body, and the body now contains several independent
operation chains that a scheduler can interleave.

## Applies to

```basic
DIM a%(0 TO 99, 0 TO 99), b%(0 TO 99), i%, j%, s%
FOR i% = 0 TO 99
  FOR j% = 0 TO 99
    s% = s% + a%(i%, j%) * b%(j%)
  NEXT
NEXT
```

## Today

`b%(j%)` is re-read for every outer iteration — 10 000 loads for 100 distinct
values.

## Planned (outer unrolled by 2)

```basic
FOR i% = 0 TO 99 STEP 2
  FOR j% = 0 TO 99
    t% = b%(j%)                       ' loaded once, used twice
    s% = s% + a%(i%, j%) * t%
    s% = s% + a%(i%+1, j%) * t%
  NEXT
NEXT
```

## What it needs

- Legality: jamming is fusion of the unrolled inner copies, so it needs the same
  dependence check ([O0172](O0172-loop-dependence-analysis.md)) — in particular
  no dependence from a later outer iteration back to an earlier one within the
  jammed body.
- Register pressure grows with the unroll factor, so it needs
  [O0176](O0176-register-pressure-scheduling.md) and a cost model
  ([O0129](O0129-unroll-factor-cost-model.md)).
- Both counters' post-loop values must be exactly what the original nest leaves,
  including the outer loop's remainder when the trip count is odd.
