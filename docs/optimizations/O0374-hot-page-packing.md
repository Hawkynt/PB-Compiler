# O0374 — Hot page packing

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0375](O0375-working-set-minimization.md), [O0371](O0371-steady-state-clustering.md), [O0400](O0400-page-boundary-outlining.md) |

## The idea

Pack the hottest code **densely** into as few virtual-memory pages as possible.
Density, not order: two hot blocks separated by a cold one occupy two pages'
worth of residency even though they are adjacent in the listing.

This is the objective BBT was built around — optimizing the working-set size of
a paged application — and it remains what sample-based PGO aims at today.

## What it needs

- Block counts and placeable fragments
  ([O0360](O0360-basic-block-fragments.md)).
- The page size from the target model — and the honest note that **real-mode DOS
  does not page at all**: on an 8086 the equivalent objective is minimizing code
  bytes fetched and taken transfers, which is
  [O0365](O0365-maximum-weighted-fallthrough.md). Page packing becomes real from
  the 386 era onward, under a memory manager or a protected-mode host.
