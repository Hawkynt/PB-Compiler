# O0377 — Instruction-cache-set-aware placement

| | |
|---|---|
| **Status** | ⬜ Planned (486 and later) |
| **Stage** | Layout |
| **Related** | [O0326](O0326-cache-conflict-padding.md), [O0374](O0374-hot-page-packing.md), [O0174](O0174-target-cost-models.md) |

## The idea

A set-associative instruction cache maps addresses to sets by their middle bits.
Two hot procedures whose addresses differ by an exact multiple of the way size
land in the **same set** and evict each other on every alternation, however small
they are.

It is the code-side twin of the data-side conflict problem
([O0326](O0326-cache-conflict-padding.md)), and the fix is the same: shift one of
them.

## What it needs

- Cache geometry (size, associativity, line size) from the target model
  ([O0174](O0174-target-cost-models.md)).
- A conflict-aware placement step *after* clustering: the clusters decide who is
  near whom, this decides the exact offsets.
- Nothing to do on an 8086/286, which have no instruction cache — only a
  prefetch queue, whose concern is [O0365](O0365-maximum-weighted-fallthrough.md).
