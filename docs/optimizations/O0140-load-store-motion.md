# O0140 — Load hoisting and store sinking

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end / scheduler |
| **Related** | [O0060](O0060-memory-ssa.md), [O0038](O0038-instruction-scheduling.md), [O0173](O0173-speculative-load-hoisting.md) |
| **Split into** | [O0253](O0253-store-sinking.md) |

## The idea

Move a load **earlier** so its latency overlaps independent work, and a store
**later** so it does not stall the instructions behind it. Both are only legal
across memory operations that provably cannot alias — which is why this waits on
[O0060](O0060-memory-ssa.md).

The scheduler already does this *within* a window over recorded instructions
([O0038](O0038-instruction-scheduling.md)); what is missing is motion across
statement and block boundaries, where the alias question actually has to be
answered.

## Applies to

```basic
DIM i%, a%(0 TO 99), b%(0 TO 99), t%
FOR i% = 0 TO 99
  b%(i%) = t% * 3            ' store
  t% = a%(i%)                ' load, independent of the store above
NEXT
```

## Today

The store to `b%()` and the load from `a%()` are emitted in source order and no
pass will swap them, because nothing proves the two arrays are distinct.

## Planned

With `a%()` and `b%()` proven non-aliasing, the load is hoisted above the store
and its latency overlaps the store's address computation.

## What it needs

- **Alias analysis** ([O0171](O0171-alias-analysis.md)) and memory SSA
  ([O0060](O0060-memory-ssa.md)) — the entire content of the pass is the legality
  question.
- Exception ordering: under `$ERROR BOUNDS` a hoisted load may raise Error 9
  *earlier* than the program would have, which is observable. So a hoist across
  a potentially-trapping operation needs either a proof or a guard
  ([O0173](O0173-speculative-load-hoisting.md)).
- A cost model — hoisting a load lengthens its live range and can cause a spill
  ([O0176](O0176-register-pressure-scheduling.md)).
