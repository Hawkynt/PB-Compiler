# O0130 — Runtime trip-count versioning

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0063](O0063-duff-unrolling.md), [O0129](O0129-unroll-factor-cost-model.md), [O0026](O0026-auto-vectorization.md) |

## The idea

When the trip count is unknown at compile time, emit **several versions** of the
loop and select one at run time: a scalar version for tiny counts, a moderately
unrolled one for medium, and a heavily unrolled or vectorized one for large.
A single compare picks the path.

This is what makes vectorization usable on variable-length data: today
[O0026](O0026-auto-vectorization.md) requires a constant trip count precisely
because it cannot generate the tail for an unknown one.

## Applies to

```basic
$CPU 80586 MMX
$OPTIMIZE SPEED
DIM i%, n%, a%(0 TO 999), b%(0 TO 999), c%(0 TO 999)
FOR i% = 0 TO n%
  c%(i%) = a%(i%) + b%(i%)
NEXT
```

## Today

Not vectorized at all — `n%` is not a constant.

## Planned

```asm
    mov     ax, [n]
    cmp     ax, 0008h
    jl      ScalarLoop       ; tiny: the scalar version wins
    ...                      ; vector kernel + tail
```

## What it needs

- **Code size**: several versions of every loop is the obvious cost, so this is
  `$OPTIMIZE SPEED`-only and bounded by a budget.
- A runtime **tail** for the vector version ([O0146](O0146-vector-tail.md)),
  since the fully-unrolled tail only works for a constant remainder.
- The crossover thresholds are per target and per loop body — another consumer
  of [O0174](O0174-target-cost-models.md).
