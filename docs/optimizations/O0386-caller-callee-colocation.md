# O0386 — Caller/callee hot-path co-location

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0361](O0361-weighted-call-graph-clustering.md), [O0363](O0363-interprocedural-block-placement.md), [O0387](O0387-return-continuation-clustering.md) |

## The idea

Clustering by function start ([O0361](O0361-weighted-call-graph-clustering.md))
places two procedures near each other. That is coarser than it needs to be: what
should be adjacent is the caller's **hot call site** and the callee's **hot
entry** — which may be deep inside two large procedures.

Block-granular placement ([O0363](O0363-interprocedural-block-placement.md))
makes the finer decision possible.

## What it needs

- Block-level call-edge counts, not just function-level ones
  ([O0268](O0268-profile-collection.md)).
- A placement objective over **fragments**, with the call edge weighted like any
  other control-flow edge — at which point co-location falls out of
  [O0365](O0365-maximum-weighted-fallthrough.md) rather than being a separate
  rule.
