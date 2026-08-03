# O0290 — Temporary reuse across loop iterations

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0286](O0286-allocation-elimination.md), [O0009](O0009-string-temp-economy.md), [O0329](O0329-array-contraction.md) |

## The idea

A temporary allocated and freed inside a loop body is allocated and freed **once
per iteration**. If its size is stable, the storage can be allocated once before
the loop and reused — turning N allocations into one.

This is the general form of what [O0208](O0208-inplace-literal-append.md)
achieves for the specific self-append shape.

## Applies to

```basic
DIM i%, line$
FOR i% = 1 TO 1000
  line$ = "row " + STR$(i%)  ' a fresh temp every iteration
  PRINT line$
NEXT
```

## What it needs

- A **size bound** that holds across iterations, or a grow-on-demand buffer that
  keeps its allocation between passes.
- Proof that the value does not escape the iteration
  ([O0260](O0260-escape-analysis.md)) — if the loop stores it into an array, each
  iteration genuinely needs its own storage.
- The heap's topmost-block behavior means a reused buffer also stops the
  fragmentation that per-iteration allocate/free causes, which is a second,
  larger win on long runs.
