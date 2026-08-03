# O0317 — False-sharing avoidance

| | |
|---|---|
| **Status** | ⬜ Planned — hosted back ends only (see [O0311](O0311-parallel-loop-versioning.md)) |
| **Stage** | Data layout |
| **Related** | [O0312](O0312-parallel-reduction.md), [O0326](O0326-cache-conflict-padding.md), [O0321](O0321-field-reordering.md) |

## The idea

Per-worker accumulators placed adjacently share a cache line, so every worker's
write invalidates every other worker's copy — the classic false-sharing
collapse, where adding threads makes the program slower. Padding each
accumulator to its own line fixes it.

## Applies to

```basic
' one partial sum per worker, laid out contiguously
DIM partial&(0 TO 7)
```

## What it needs

- The cache-line size from the target model
  ([O0174](O0174-target-cost-models.md)) — 64 bytes on a modern host, and
  **irrelevant** on every DOS-era target, which has no coherent cache and no
  second core.
- A layout facility for compiler-created per-worker data, which is the same
  padding machinery [O0326](O0326-cache-conflict-padding.md) needs.
