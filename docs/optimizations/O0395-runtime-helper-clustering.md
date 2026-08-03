# O0395 — Runtime helper clustering

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [P0001](P0001-runtime-trimming.md), [O0361](O0361-weighted-call-graph-clustering.md), [O0372](O0372-shutdown-code-isolation.md) |

## The idea

Runtime routines that are **used together** should sit together: string
allocation, concatenation and release; the number formatter and its digit
helpers; the array engine and its bounds reporter. A string-heavy program calls
`StrMem`, `StrCat` and `StrFree` in the same breath, and today they are placed by
section order rather than by affinity.

[P0001](P0001-runtime-trimming.md) already selects the minimal section set; this
orders what survives.

## What it needs

- Call affinity between runtime labels, which the trimmer's reachability graph
  **already computes** — it maps every section to the labels it references, which
  is exactly the affinity graph.
- Fragment-level placement for the runtime, which is currently emitted as whole
  sections ([O0360](O0360-basic-block-fragments.md)).
