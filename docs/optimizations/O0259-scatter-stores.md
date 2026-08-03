# O0259 — Scatter stores

| | |
|---|---|
| **Status** | ⬜ Planned (AVX-512 only) |
| **Stage** | Emitter |
| **Related** | [O0151](O0151-gather-scatter.md), [O0172](O0172-loop-dependence-analysis.md) |
| **Split from** | [O0151](O0151-gather-scatter.md) |

## The idea

The write counterpart of a gather: one instruction stores N lanes to N
independent addresses, vectorizing loops that write through an index array.

## Applies to

```basic
DIM i%, idx%(0 TO 999), src%(0 TO 999), dst%(0 TO 999)
FOR i% = 0 TO 999
  dst%(idx%(i%)) = src%(i%)
NEXT
```

## What it needs

- **Dependence analysis must prove no two lanes write the same address** within
  one vector step ([O0172](O0172-loop-dependence-analysis.md)) — otherwise the
  result depends on which lane wins, which the scalar loop defines by order and
  the scatter does not.
- Under `$ERROR BOUNDS`, every scattered index still needs its check in element
  order, which usually defeats the transform.
- Honest cost modelling: scatter is slow on every implementation that has it.
