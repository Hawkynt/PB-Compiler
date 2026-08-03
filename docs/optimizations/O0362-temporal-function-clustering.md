# O0362 — Temporal function clustering

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Linker layout |
| **Related** | [O0361](O0361-weighted-call-graph-clustering.md), [O0370](O0370-startup-code-clustering.md), [O0373](O0373-phase-aware-layout.md) |

## The idea

Procedures that execute **during the same time window** belong together, even
when neither calls the other. Two independent helpers used by the same phase of
the program share a page; a caller and a callee used in different phases do not,
despite the call edge.

This is what BBT-class tools measure that a call graph alone misses.

## What it needs

- A profile with **timestamps or phase markers**, not just counts
  ([O0268](O0268-profile-collection.md)) — the extra dimension is the whole
  point.
- A clustering metric over co-occurrence windows rather than over edges.
- It composes with, rather than replaces,
  [O0361](O0361-weighted-call-graph-clustering.md): call weight and temporal
  affinity are two terms of one placement objective.
