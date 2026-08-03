# O0224 — Bounded multiply stays off the FPU

| | |
|---|---|
| **Status** | ✅ Implemented (2026-08) |
| **Stage** | Emitter, on the value lattice |
| **Source** | `CodeGen/CodeGenerator.cs` — `ModularTreeBits`, `RangeBits` |
| **Gate** | `--optimize` |
| **Verified by** | scenario `BoundedMultiplyStaysOffTheFpu` |
| **Split from** | [O0016](O0016-value-fact-analysis.md) |

## What it is

PB promotes an integer multiply to floating point, so `p& = a% * b%` normally
pays `FILD` for each operand, `FMUL`, and `FISTP` — the FPU being the only place
a 16×16 product that might exceed 32 bits can be formed exactly.

But when the operand **ranges** prove the product fits int32, no overflow is
possible, the promoted Double holds the value exactly (a sub-2³¹ result is far
inside the 2⁵³ mantissa), and the native 32-bit `IMUL` gives the identical
result.

`ModularTreeBits` counts each leaf by the **tighter** of its type width and its
proven range (`RangeBits`), so `(i% AND 255) * (j% AND 255)` — each operand
≤ 255, product ≤ 65 025 — demotes off the x87 entirely, where the bare types
(31 + 31 bits) would have kept it there.

## Sample

```basic
DIM i%, j%, p&
p& = (i% AND 255) * (j% AND 255)
```

## Why it is safe

Three facts have to hold together: the product cannot overflow, the Double
represents it exactly, and the integer instruction produces the same bits. This
is the multiply half of the LONG model whose add/subtract half is the native
wrap documented in `docs/QUIRKS.md`; both were verified byte-identical against
genuine PBC 3.50.
