# O0361 — Weighted call-graph function clustering

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Linker layout |
| **Related** | [O0360](O0360-basic-block-fragments.md), [O0268](O0268-profile-collection.md), [O0362](O0362-temporal-function-clustering.md) |

## The idea

Build a call graph weighted by observed transitions and place procedures that
frequently call one another **adjacent** in the image. This is the first and
cheapest layout decision, and on a paged target it is the one that shrinks the
working set most.

## What it needs

- Call-edge counts ([O0268](O0268-profile-collection.md)) and placeable
  fragments ([O0360](O0360-basic-block-fragments.md)).
- A clustering algorithm over the weighted graph — the classic
  Pettis-Hansen greedy edge merge is sufficient and cheap.
- Without a profile, the static approximation is call depth plus loop nesting,
  which is what [O0104](O0104-block-placement.md) infers.
