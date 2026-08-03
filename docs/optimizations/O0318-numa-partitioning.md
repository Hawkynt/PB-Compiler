# O0318 — NUMA-aware partitioning

| | |
|---|---|
| **Status** | ⬜ Planned — hosted back ends only (see [O0311](O0311-parallel-loop-versioning.md)) |
| **Stage** | Runtime + mid-end |
| **Related** | [O0311](O0311-parallel-loop-versioning.md), [O0327](O0327-data-transposition.md), [O0174](O0174-target-cost-models.md) |

## The idea

On a multi-socket host, memory has locality: a worker should process the slice of
the data that lives in **its own** node's memory, and the data should be
allocated (first-touched) accordingly.

## Applies to

Large array computations under the hosted back ends —
`--emit-c`/`--emit-llvm` output compiled for a server, which is the only context
in which this compiler can encounter a NUMA machine at all.

## What it needs

- A host allocator and thread-affinity API; nothing in the DOS runtime maps onto
  this.
- Work partitioning aligned with the memory partitioning, i.e. the loop slicing
  of [O0311](O0311-parallel-loop-versioning.md) has to agree with the allocation
  policy rather than being chosen independently.
- Stated plainly: this is the **least applicable** entry in the whole catalog for
  a 16-bit DOS compiler, and is listed for completeness of the family.
