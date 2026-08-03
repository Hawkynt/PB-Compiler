# O0315 — Pipeline parallelization

| | |
|---|---|
| **Status** | ⬜ Planned — hosted back ends only (see [O0311](O0311-parallel-loop-versioning.md)) |
| **Stage** | Mid-end |
| **Related** | [O0123](O0123-loop-distribution.md), [O0128](O0128-software-pipelining.md), [O0314](O0314-task-graph-extraction.md) |

## The idea

Producer, transformer and consumer stages of a loop run **concurrently**, with
buffering between them — the thread-level analogue of software pipelining
([O0128](O0128-software-pipelining.md)), which overlaps the same stages within
one instruction stream.

## Applies to

```basic
FOR i% = 0 TO n%
  raw$ = ReadRecord$(i%)     ' I/O bound
  parsed% = Parse%(raw$)     ' CPU bound
  CALL Store(parsed%)        ' I/O bound
NEXT
```

## What it needs

- Loop distribution ([O0123](O0123-loop-distribution.md)) to separate the
  stages, and a dependence proof that the stages communicate only through the
  buffer.
- A buffering policy and its cost — too small and the stages serialize, too
  large and the memory traffic dominates.
- The ordering constraint: PB's I/O statements are observably ordered, so a
  pipeline may only reorder work that is not itself observable.
