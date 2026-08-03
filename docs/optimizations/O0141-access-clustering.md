# O0141 — Memory access clustering

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Scheduler |
| **Related** | [O0038](O0038-instruction-scheduling.md), [O0136](O0136-adjacent-access-merging.md), [O0174](O0174-target-cost-models.md) |

## The idea

Group memory operations that touch nearby addresses so the bus, the cache line
and (on later targets) the prefetcher are used well. Clustering also creates the
adjacency that [O0136](O0136-adjacent-access-merging.md) needs in order to merge
accesses at all.

The assembler-level scheduler already *clusters memory and ALU work* as a
heuristic; what it does not do is order the memory operations among themselves
by **address**, because its aliasing model is coarse (direct cell / `[BP+disp]`
/ unknown-indexed).

## Applies to

```basic
DIM p AS Rgb, q AS Rgb
p.r = q.r
p.b = q.b
p.g = q.g                    ' out of address order
```

## Today

Emitted in source order: `+0`, `+2`, `+1`.

## Planned

Reordered to `+0`, `+1`, `+2` — three accesses on one cache line in order, and
now adjacent enough for [O0136](O0136-adjacent-access-merging.md) to merge.

## What it needs

- A finer **alias/offset model** in the scheduler's records: it knows the base
  kind but not the numeric offset relationship between two accesses on the same
  base.
- Per-target benefit: on an 8086 the win is bus-cycle pairing and the merge
  opportunity; on a cached target it is line locality
  ([O0174](O0174-target-cost-models.md)).
- The reordering must remain output-preserving in the strict sense the current
  scheduler guarantees — permuting whole instruction blocks with no fixup
  changes.
