# O0274 — Profile-guided code layout

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Assembler / linker |
| **Related** | [O0104](O0104-block-placement.md), [O0268](O0268-profile-collection.md), [O0360](O0360-basic-block-fragments.md) |

## The idea

Arrange functions and blocks by observed execution so that the hot path is
contiguous — improving instruction-cache, TLB and branch-predictor behavior on
386-era and later targets, and prefetch-queue and code-fetch traffic on an 8086.

This entry is the *umbrella intent*; the concrete transformations are the
binary-layout family, [O0360](O0360-basic-block-fragments.md) through
[O0406](O0406-layout-assertion-battery.md), which is where Microsoft's BBT/LEGO
and Vulcan work aimed and where modern sample-based PGO still aims.

## What it needs

- Edge counts ([O0268](O0268-profile-collection.md)) and relocatable block
  fragments with stable IDs ([O0360](O0360-basic-block-fragments.md)).
- A placement algorithm over the weighted CFG/call graph
  ([O0361](O0361-weighted-call-graph-clustering.md),
  [O0365](O0365-maximum-weighted-fallthrough.md)).
- A **post-layout cleanup pass** — layout must not be the last step, because it
  creates new short branches, new fall-throughs and new merge opportunities
  ([O0382](O0382-post-layout-branch-relaxation.md)).
