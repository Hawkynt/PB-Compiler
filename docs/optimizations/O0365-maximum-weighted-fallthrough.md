# O0365 — Maximum weighted fall-through

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0364](O0364-hot-path-block-chaining.md), [O0094](O0094-branch-inversion.md), [O0406](O0406-layout-assertion-battery.md) |

## The idea

State the layout problem as an objective rather than a heuristic: choose the
block order that **maximizes the execution-weighted number of fall-through
edges**. Every edge that becomes a fall-through is a taken transfer removed —
which on an 8086 is a prefetch-queue flush avoided, and on a modern core a
branch not predicted.

It is the metric that makes layout measurable
([O0406](O0406-layout-assertion-battery.md)) rather than a matter of taste.

## What it needs

- Edge weights ([O0268](O0268-profile-collection.md)).
- An ordering algorithm; the problem is NP-hard in general, so the greedy chain
  of [O0364](O0364-hot-path-block-chaining.md) is the practical approximation.
- The **weighted fall-through ratio** exposed as a reportable statistic, so a
  layout change can be judged rather than assumed.
