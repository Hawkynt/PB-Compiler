# O0125 — Loop skewing

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0122](O0122-loop-interchange.md), [O0124](O0124-loop-tiling.md), [O0172](O0172-loop-dependence-analysis.md) |

## The idea

Some nests carry a **diagonal** dependence — each element depends on its
neighbours from the previous row — which blocks both interchange and
vectorization. Skewing the iteration space (running `j` from `i` rather than
from 0, and adjusting the accesses) turns the diagonal wavefront into a
rectangular one, after which the inner loop is dependence-free.

The canonical example is a relaxation or blur step, which is exactly the shape a
DOS-era graphics program uses for smoothing.

## Applies to

```basic
DIM g%(0 TO 199, 0 TO 319), i%, j%
FOR i% = 1 TO 198
  FOR j% = 1 TO 318
    g%(i%, j%) = (g%(i%-1, j%) + g%(i%, j%-1) + g%(i%, j%) ) \ 3
  NEXT
NEXT
```

## Today

The inner loop carries a dependence on `g%(i%, j%-1)`, so nothing vectorizes and
no pointer stepping applies to the write.

## Planned

The iteration space is skewed so that each inner iteration is independent of its
neighbours, exposing a vectorizable wavefront.

## What it needs

- Full **dependence-vector** analysis ([O0172](O0172-loop-dependence-analysis.md))
  — skewing is chosen from the dependence directions, so it cannot be done by
  pattern matching.
- The index rewriting must preserve the exact iteration set and the counters'
  post-loop values.
- It is the most analysis-heavy item in this family and only pays where
  vectorization follows, so it is downstream of
  [O0026](O0026-auto-vectorization.md) being generalized
  ([O0074](O0074-wider-vectorization.md)).
