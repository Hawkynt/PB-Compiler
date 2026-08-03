# O0144 — Interleaved-access vectorization

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0143](O0143-slp-vectorization.md), [O0026](O0026-auto-vectorization.md), [R0004](R0004-asm-intrinsics.md) |

## The idea

Data is often stored **interleaved** — RGB triples, XY coordinate pairs, stereo
samples — so a loop over one channel strides rather than walks. Vectorizing such
a loop means loading several consecutive vectors and **de-interleaving** them
with the unpack/shuffle family (`PUNPCKLBW`, `PUNPCKHBW`, …), operating on the
separated channels, then re-interleaving to store.

The pack/unpack instructions are already implemented in the assembler
([R0004](R0004-asm-intrinsics.md)); what is missing is a vectorizer that knows
to use them.

## Applies to

```basic
TYPE Rgb
  r AS BYTE
  g AS BYTE
  b AS BYTE
END TYPE
DIM img(0 TO 999) AS Rgb, i%
FOR i% = 0 TO 999
  img(i%).r = 255 - img(i%).r      ' one channel, stride 3
NEXT
```

## Today

Not vectorized: the access is strided, and the recognizer requires unit stride
over a 2-byte-element array.

## Planned

Load three vectors' worth of interleaved bytes, de-interleave to isolate the red
lanes, negate them packed, re-interleave, store.

## What it needs

- **Cost modelling above all.** De-interleaving costs several shuffles per
  vector, so the arithmetic in between must be substantial enough to pay for
  them — this is the family where naive vectorization most reliably produces
  impressive-looking, slower code.
- A stride/group recognizer over the access pattern, and the same wrap-per-lane
  correctness argument as [O0026](O0026-auto-vectorization.md).
