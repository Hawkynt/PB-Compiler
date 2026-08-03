# O0075 — Silent fixed-point arithmetic

| | |
|---|---|
| **Status** | ⬜ Planned (the FOR-counter case is done — [O0037](O0037-fixed-point-for-counters.md)) |
| **Stage** | Mid-end analysis + emitter |
| **Related** | [O0037](O0037-fixed-point-for-counters.md), [O0012](O0012-float-demotion.md), [O0016](O0016-value-fact-analysis.md) |

## The idea

Where float values provably carry a **constant scale** — monetary
`* 0.25` / `* 0.01` chains, interpolation accumulators, `FOR` loops with a
fractional constant `STEP` — the whole computation can run in scaled `LONG`
fixed-point, converting only at the observation boundary (`PRINT`, `STR$`, a
store to an escaping location).

This is the classic DOS demo-effect win: a `STEP 0.25` loop becomes an integer
counter with a 2-bit fraction, and the interpolation inside it becomes integer
adds.

## Applies to

```basic
DIM x, dx, i%
x = 0 : dx = 0.25
FOR i% = 1 TO 100
  x = x + dx
  PSET (i%, INT(x)), 15
NEXT
```

## Today

Every iteration pays `FLD`/`FADD`/`FSTP` plus a truncation round trip.

## Planned

```basic
DIM xFixed&, dxFixed&        ' scaled by 4
xFixed& = 0 : dxFixed& = 1
FOR i% = 1 TO 100
  xFixed& = xFixed& + dxFixed&
  PSET (i%, xFixed& \ 4), 15
NEXT
```

## What it needs

- A scale-inference analysis: propagate "this value is `k × 2⁻ⁿ`" through
  `+ - *` and constant `/`, and find the common scale for a connected component
  of values.
- **Bit-exactness must be provable**, not likely. Power-of-two scales always
  are; decimal scales only when every intermediate stays within the exact-integer
  range of the original float type — otherwise the x87 original's rounding could
  differ by an ULP and leak into `PRINT` output.
- Conversion insertion at every observation boundary, with the same
  formatting-equivalence argument [O0012](O0012-float-demotion.md) relies on.
