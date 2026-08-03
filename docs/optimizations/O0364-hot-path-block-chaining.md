# O0364 — Hot-path block chaining

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0365](O0365-maximum-weighted-fallthrough.md), [O0104](O0104-block-placement.md), [O0389](O0389-hot-trace-layout.md) |

## The idea

Order blocks by following the **highest-frequency control-flow edge** out of
each: start at the entry, chain to the hottest successor, continue until the
chain ends, then start a new chain from the hottest unplaced block.

The result is that the common path is one straight run of bytes, with the rare
edges becoming the taken branches.

## What it needs

- Edge counts ([O0268](O0268-profile-collection.md)); with static estimates
  ([O0104](O0104-block-placement.md)) the same algorithm still improves on
  source order.
- Branch-sense inversion where the chain requires it
  ([O0094](O0094-branch-inversion.md)), and re-relaxation afterwards
  ([O0382](O0382-post-layout-branch-relaxation.md)).
