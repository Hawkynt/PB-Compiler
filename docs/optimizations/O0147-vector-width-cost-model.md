# O0147 — Vector width chosen by cost model

| | |
|---|---|
| **Status** | ⬜ Planned (the width is currently taken from the `$CPU` flags alone) |
| **Stage** | Emitter policy |
| **Related** | [O0026](O0026-auto-vectorization.md), [O0174](O0174-target-cost-models.md), [O0130](O0130-trip-count-versioning.md) |

## The idea

Today the vectorizer emits the **widest** width the `$CPU` feature set allows —
MMX → SSE2 → AVX2 → AVX-512, four lanes to thirty-two. Wider is not always
better:

- **transition costs**: mixing MMX and x87 needs `EMMS`; mixing VEX and legacy
  SSE costs a penalty on several microarchitectures;
- **frequency throttling**: AVX-512 code can lower the clock on some parts, so a
  short loop finishes later at 512 bits than at 256;
- **register pressure and spill traffic** grow with the width;
- **problem size**: a 20-element loop cannot fill a 32-lane vector, and the
  prologue/tail dominate.

## Applies to

```basic
$CPU 80586 AVX512
$OPTIMIZE SPEED
DIM i%, a%(0 TO 19), b%(0 TO 19), c%(0 TO 19)
FOR i% = 0 TO 19             ' 20 elements: one 32-lane vector plus a tail
  c%(i%) = a%(i%) + b%(i%)
NEXT
```

## Today

The widest available register is used regardless of trip count.

## Planned

The width is chosen per loop from lanes-versus-trips, transition cost and
register pressure — here, 8 or 16 lanes with a short tail beats 32 lanes that
are half empty.

## What it needs

- [O0174](O0174-target-cost-models.md), with per-microarchitecture transition
  and throttling data.
- Trip-count information ([O0131](O0131-exact-trip-count.md)), and the option to
  version the loop by size ([O0130](O0130-trip-count-versioning.md)).
