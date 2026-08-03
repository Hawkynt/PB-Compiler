# O0253 — Store sinking

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end / scheduler |
| **Related** | [O0140](O0140-load-store-motion.md), [O0118](O0118-loop-dead-store-elimination.md), [O0060](O0060-memory-ssa.md) |
| **Split from** | [O0140](O0140-load-store-motion.md) |

## The idea

Move a store **later** — past independent work, out of a conditional, or out of
a loop — so it does not stall the instructions behind it and, in the loop case,
happens once instead of every iteration.

Sinking a store out of a branch also merges two stores into one at the join,
which is the store counterpart of if-conversion.

## Applies to

```basic
DIM i%, acc%, a%(0 TO 99)
FOR i% = 0 TO 99
  acc% = acc% + a%(i%)       ' the store to acc% only matters after the loop
NEXT
```

## What it needs

- **Alias analysis** ([O0171](O0171-alias-analysis.md)): nothing between the
  original and the sunk position may read the location.
- Every **exit path** must see the value — an `EXIT FOR`, a `GOTO` out, or an
  error handler makes the intermediate state observable, which is why register
  residency ([O0005](O0005-register-residency.md)) flushes on every exit and this
  pass must do the same.
