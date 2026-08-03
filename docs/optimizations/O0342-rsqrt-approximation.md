# O0342 — Reciprocal square-root approximation

| | |
|---|---|
| **Status** | ⬜ Planned — fast-math mode only |
| **Stage** | Emitter |
| **Related** | [O0341](O0341-reciprocal-approximation.md), [O0343](O0343-transcendental-specialization.md) |

## The idea

`1 / SQR(x)` — the normalization step of every vector length computation — maps
onto a hardware reciprocal-square-root estimate plus Newton-Raphson refinement,
instead of a square root followed by a division.

On the x87 both `FSQRT` and `FDIV` are slow, so even without an estimate
instruction the *algebraic* rewrite (compute `1/SQR(x)` once and multiply)
already pays where the value is reused.

## Applies to

```basic
DIM x!, y!, len!, nx!, ny!
len! = SQR(x! * x! + y! * y!)
nx! = x! / len!
ny! = y! / len!              ' two divides by the same root
```

## What it needs

- Fast-math mode for the estimate form
  ([O0340](O0340-fma-contraction.md)); the reuse form
  ([O0338](O0338-reciprocal-sequence-reuse.md)) needs it too, since `x * (1/L)`
  is not `x / L` bit for bit.
- Zero and negative inputs must behave as PB's `SQR` does — including raising
  the same error — which the estimate path does not do on its own.
