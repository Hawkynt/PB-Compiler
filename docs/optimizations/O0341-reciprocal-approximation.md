# O0341 — Reciprocal approximation with refinement

| | |
|---|---|
| **Status** | 🟨 Partial — IR legality and LLVM target freedom implemented; estimate/refinement sequence remains target-selected |
| **Stage** | IR middle end + target lowering |
| **Gate** | Optimizer + `$OPTIMIZE SPEED` / `-OZF` |
| **IR** | `IrFastMathFlags.AllowReciprocal`, applied to `FDiv` by `FpFastMath`; emitted as LLVM `arcp` |
| **Related** | [O0338](O0338-reciprocal-sequence-reuse.md), [O0340](O0340-fma-contraction.md), [O0342](O0342-rsqrt-approximation.md) |

## What is implemented

The middle end records that an eligible division may be transformed through a
reciprocal. On the LLVM path this is emitted as `arcp`, which gives the target
optimizer permission to choose a reciprocal estimate and whatever refinement
sequence is appropriate for the selected ISA and precision.

`FpFastMath` also consumes the same legality directly for repeated divisions by
the same SSA divisor: [O0345](O0345-common-denominator-factoring.md) creates one
`1/d` and replaces the other divisions by multiplications.

```basic
DIM a!, b!, r!
r! = a! / b!
```

The 16-bit x87 route does not synthesize an estimate: there is no x87 reciprocal
estimate instruction, and replacing `FDIV` by a software Newton sequence would
not be a general win.

## Still target-specific

The IR intentionally does not hard-code "one Newton step for SINGLE, two for
DOUBLE". Estimate accuracy, denormal behavior and the profitable refinement
count are target properties. LLVM receives the legality contract; its target
lowering owns that choice.

Ordinary optimization carries no `arcp` flag, so exact division remains
required there.
