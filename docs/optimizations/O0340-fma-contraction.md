# O0340 — Fused multiply-add contraction

| | |
|---|---|
| **Status** | ⬜ Planned — hosted back ends only (no x86 FMA before Haswell; the x87 has none) |
| **Stage** | Emitter |
| **Related** | [O0344](O0344-fp-reassociation.md), [O0347](O0347-mixed-precision.md), [docs/BACKENDS.md](../BACKENDS.md) |

## The idea

`a*b + c` becomes a single fused multiply-add: one instruction, one rounding
instead of two — usually *more* accurate, but **different**.

## Applies to

```basic
DIM a!, b!, c!, r!
r! = a! * b! + c!
```

## Why it needs a fast-math mode

The result differs from the two-step computation, because the intermediate
product is not rounded. "Usually more accurate" is not the same as "identical",
and this compiler's bar for the DOS targets is **byte-identical output against
the genuine compiler** — which has no FMA and rounds twice.

So: available only on the hosted back ends, only under an explicit fast-math
declaration, and never for a dialect under oracle verification.

## What it needs

- An FP mode model — strict (the default, bit-exact) versus fast (contraction,
  reassociation, reciprocals allowed) — shared with
  [O0341](O0341-reciprocal-approximation.md) and
  [O0344](O0344-fp-reassociation.md).
- Target detection for the FMA instruction set on the C/LLVM path.
