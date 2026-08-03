# O0316 — Parallel loop collapse

| | |
|---|---|
| **Status** | ⬜ Planned — hosted back ends only (see [O0311](O0311-parallel-loop-versioning.md)) |
| **Stage** | Mid-end |
| **Related** | [O0122](O0122-loop-interchange.md), [O0311](O0311-parallel-loop-versioning.md), [O0172](O0172-loop-dependence-analysis.md) |

## The idea

A nest of loops whose individual trip counts are too small to divide among
workers becomes **one** flattened iteration space with the product of the counts
— enough work to schedule.

```
FOR y = 0 TO 3 : FOR x = 0 TO 3     ->     FOR k = 0 TO 15
```

with `y = k \ 4` and `x = k MOD 4` — both cheap when the extent is a power of
two ([O0004](O0004-strength-reduction.md)).

## Applies to

```basic
FOR y% = 0 TO 3
  FOR x% = 0 TO 3
    CALL ExpensiveWork(x%, y%)
  NEXT
NEXT
```

## What it needs

- Rectangular bounds (the inner limit must not depend on the outer counter) and
  independence across the whole collapsed space
  ([O0172](O0172-loop-dependence-analysis.md)).
- Both counters' **post-loop values** reconstructed exactly, as for every loop
  transform.
- The index arithmetic must not cost more than the scheduling gains — which for
  non-power-of-two extents means a divide per iteration unless
  [O0056](O0056-reciprocal-division.md) is available.
