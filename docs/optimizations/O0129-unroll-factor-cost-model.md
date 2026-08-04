# O0129 — Unroll factor from a target cost model

| | |
|---|---|
| **Status** | 🟡 Partial — the full-unroll trip budget is now a per-target call through the [O0174](O0174-target-cost-models.md) cost model (`Cost.MaxFullUnrollTrips`): the fetch-bound 8086/286/386 keep four copies, a 486+ takes eight. The register/latency-driven *partial* unroll factor for larger loops remains |
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

## Now

The full-unroll trip budget is no longer the bare constant 4 — it is
`Cost.MaxFullUnrollTrips`, which the [O0174](O0174-target-cost-models.md) model
answers per target: four on the fetch-bound 8086/286/386 (more instruction bytes
throttle the prefetch queue that is their bottleneck), eight on a 486 or later
whose instruction cache absorbs the wider body and profits more from deleting the
per-iteration compare/branch. The default 8086 tier keeps four, so faithful output
is byte-identical; a `$CPU 80486`+ speed build unrolls a six- or eight-trip tiny
loop the 8086 leaves rolled (a cross-tier regression test pins the size split, and
the ≤4-copy path stays DOSBox-verified on and off).

```
if (trip count is constant && trips <= Cost.MaxFullUnrollTrips && body is small) unroll fully
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
