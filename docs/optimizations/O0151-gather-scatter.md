# O0151 — Gather and scatter selection

| | |
|---|---|
| **Status** | ⬜ Planned (no gather/scatter instruction before AVX2/AVX-512) |
| **Stage** | Emitter |
| **Related** | [O0026](O0026-auto-vectorization.md), [O0144](O0144-interleaved-access-vectorization.md), [O0172](O0172-loop-dependence-analysis.md) |
| **Split into** | [O0259](O0259-scatter-stores.md) |

## The idea

A loop that indexes an array **indirectly** — `a(idx(i))` — cannot use a
contiguous vector load. AVX2's gather and AVX-512's scatter address that
directly: one instruction fetches (or stores) N elements from N independent
addresses.

The classic BASIC case is a palette or lookup table applied to an image.

## Applies to

```basic
DIM i%, src(0 TO 999) AS BYTE, pal%(0 TO 255), dst%(0 TO 999)
FOR i% = 0 TO 999
  dst%(i%) = pal%(src(i%))       ' indirect: a gather
NEXT
```

## Today

One indexed load per element; the loop is not vectorizable at all.

## Planned

A vector of indices is built from `src`, and one gather fetches the
corresponding palette entries.

## What it needs

- **Honest cost modelling.** Gather is not fast on most implementations — it is
  often barely better than N scalar loads, and on some parts it is worse. It
  wins when it removes a dependent chain, not merely because it is one
  instruction ([O0174](O0174-target-cost-models.md)).
- For **scatter**, dependence analysis must prove no two lanes write the same
  address in one vector step, or the result depends on lane order
  ([O0172](O0172-loop-dependence-analysis.md)).
- Bounds behavior: under `$ERROR BOUNDS` each gathered index must still be
  checked, in element order, which usually defeats the transform outright.
