# O0396 — Import thunk placement

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Linker layout |
| **Related** | [O0361](O0361-weighted-call-graph-clustering.md), [docs/LINKER.md](../LINKER.md), [O0397](O0397-indirect-target-clustering.md) |

## The idea

Calls into linked units, foreign OMF objects and C-runtime routines go through
stubs. Grouping the frequently called ones — and placing each near its callers —
reduces the fetch and translation disruption a cross-module call costs.

For this compiler the relevant "imports" are `$LINK`ed `.PBU`/`.PBL` entry
points and the OMF externals resolved by the linker
(`docs/LINKER.md`).

## What it needs

- Call counts per external symbol ([O0268](O0268-profile-collection.md)).
- The linker to treat resolved externals as placeable fragments like anything
  else ([O0360](O0360-basic-block-fragments.md)) — today they are emitted where
  the object provides them.
- Modest expected payoff on a single-segment DOS image, where every call is near
  already; it grows with multi-segment images
  ([O0383](O0383-call-displacement-optimization.md)).
