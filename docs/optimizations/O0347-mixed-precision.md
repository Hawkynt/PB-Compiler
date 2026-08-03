# O0347 — Mixed-precision computation

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0012](O0012-float-demotion.md), [O0057](O0057-storage-narrowing.md), [O0346](O0346-fp-classification-simplification.md) |

## The idea

Compute parts of an expression at **narrower precision** where error analysis
proves it acceptable: `SINGLE` instead of `DOUBLE`, or `DOUBLE` instead of `EXT`.
The saving is memory traffic and — on the x87 — the load/store width, since the
arithmetic itself always happens at 80 bits internally.

The related and more valuable case is the one
[O0012](O0012-float-demotion.md) already implements: values that are not really
floats at all.

## Applies to

```basic
DIM acc AS DOUBLE, i%
FOR i% = 0 TO 999
  acc = acc + tiny!(i%)      ' SINGLE inputs, DOUBLE accumulator
NEXT
```

Here the *accumulator* genuinely wants the extra precision — which is the point:
narrowing must be proven, not assumed.

## What it needs

- A real **error analysis**, not a heuristic. On the x87 the intermediate
  precision is set by the control word, so narrowing the storage does not narrow
  the computation — which makes the analysis subtler than on a machine with
  per-instruction precision.
- Fast-math gating wherever the narrowing changes the observable result, which
  for PRINT-visible values it usually does.
