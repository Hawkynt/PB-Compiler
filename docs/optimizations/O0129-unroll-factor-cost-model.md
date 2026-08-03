# O0129 — Unroll factor from a target cost model

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end policy |
| **Related** | [O0007](O0007-loop-unrolling.md), [O0063](O0063-duff-unrolling.md), [O0174](O0174-target-cost-models.md) |

## The idea

Today's rule is a constant: constant trip count, at most 4 iterations, small
straight-line body ([O0007](O0007-loop-unrolling.md)). A real policy chooses the
factor from measurable properties:

- **register pressure** — how many live values the unrolled body needs;
- **code size** — the image budget, and on a fetch-bound target the prefetch
  cost of a larger body;
- **instruction latency and issue width** — how much independent work is needed
  to fill the pipeline;
- **trip-count estimate** — a loop that runs three times should not be unrolled
  by eight.

## Applies to

Every unrolling decision, including the vector loops
([O0026](O0026-auto-vectorization.md)) and unroll-and-jam
([O0126](O0126-unroll-and-jam.md)).

## Today

```
if (trip count is constant && trips <= 4 && body is small) unroll fully
```

## Planned

```
factor = argmax over f of  benefit(f) - cost(f)
  benefit: loop-control instructions removed, independent chains exposed
  cost:    bytes added, registers required, prefetch/i-cache pressure
```

## What it needs

- [O0174](O0174-target-cost-models.md) for the per-target constants.
- A **trip-count estimate** where the count is not constant
  ([O0131](O0131-exact-trip-count.md)), and the runtime-versioned form
  ([O0130](O0130-trip-count-versioning.md)) for the cases where the estimate is
  unavailable.
- The measurement discipline noted in
  [O0177](O0177-cycle-estimate-battery.md): instruction-count assertions cannot
  express "larger but faster", which is exactly what a good unroll factor
  usually is.
