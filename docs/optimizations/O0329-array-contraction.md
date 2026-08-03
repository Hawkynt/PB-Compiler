# O0329 — Array contraction

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0328](O0328-temporary-array-fusion.md), [O0138](O0138-overlapping-load-combining.md), [O0290](O0290-loop-temporary-reuse.md) |

## The idea

When only a **sliding window** of an array is ever live — later iterations read
just the last one or two elements — the array contracts to that many scalars.
A 1 000-element temporary becomes two registers.

## Applies to

```basic
DIM i%, t%(0 TO 999), a%(0 TO 999)
t%(0) = a%(0)
FOR i% = 1 TO 999
  t%(i%) = t%(i% - 1) + a%(i%)     ' only t%(i-1) is ever read
NEXT
PRINT t%(999)                       ' and only the last element escapes
```

becomes a single running scalar.

## What it needs

- A **live-range proof over the index space**: for every write to `t(i)`, the
  only reads are at indices within a fixed distance, and no read of the whole
  array survives the loop (the `PRINT t%(999)` above is satisfied by the final
  scalar).
- That proof is dependence analysis
  ([O0172](O0172-loop-dependence-analysis.md)) applied to a *single* array, which
  makes it one of the more tractable members of that family.
- It composes with [O0138](O0138-overlapping-load-combining.md), which carries
  the same window in registers without changing the storage.
