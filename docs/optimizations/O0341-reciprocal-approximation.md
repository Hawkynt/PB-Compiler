# O0341 — Reciprocal approximation with refinement

| | |
|---|---|
| **Status** | ⬜ Planned — fast-math mode only |
| **Stage** | Emitter |
| **Related** | [O0338](O0338-reciprocal-sequence-reuse.md), [O0340](O0340-fma-contraction.md), [O0342](O0342-rsqrt-approximation.md) |

## The idea

Replace a division with an approximate reciprocal plus one or two
Newton-Raphson refinement steps. On hardware with a fast reciprocal estimate
(`RCPPS` and friends) this is several times faster than a true divide; the x87
has `FDIV` only, so the DOS targets gain nothing.

## Applies to

```basic
DIM a!, b!, r!
r! = a! / b!
```

## What it needs

- **Fast-math mode.** The refined result is accurate to within an ULP or two,
  not exact — so it changes printed output and is inadmissible under the
  differential oracle. Same gate as [O0340](O0340-fma-contraction.md).
- A refinement-count policy: one step for single precision, two for double,
  chosen by the required accuracy.
- Special values (zero, infinity, denormals) must still behave, which the naive
  estimate-plus-refine sequence does **not** guarantee — a guard or a fallback is
  part of the lowering, not an afterthought.
