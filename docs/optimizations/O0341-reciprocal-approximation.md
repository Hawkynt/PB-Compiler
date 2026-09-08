# O0341 — Reciprocal approximation with refinement

| | |
|---|---|
| **Status** | ✅ Implemented — SPEED requests target-selected binary32/binary64 reciprocal estimates and refinement on the LLVM path |
| **Stage** | IR middle end + target lowering |
| **Gate** | Optimizer + `$OPTIMIZE SPEED` / `-OZF` |
| **IR** | `IrFastMathFlags.AllowReciprocal` + `NoInfs` on `FDiv`; emitted as LLVM `arcp ninf` plus `"reciprocal-estimates"="divf[,divd]"` |
| **Related** | [O0338](O0338-reciprocal-sequence-reuse.md), [O0340](O0340-fma-contraction.md), [O0342](O0342-rsqrt-approximation.md) |

## What is implemented

The middle end records the numerical freedom on each eligible floating-point
division. Under `$OPTIMIZE SPEED`, `FpFastMath` supplies both `AllowReciprocal`
and `NoInfs`, which LLVM receives as the per-instruction `arcp` and `ninf`
fast-math flags.

The LLVM emitter now also requests reciprocal-estimate code generation for the
precisions that actually contain eligible divisions in the function:

- binary32 emits `"reciprocal-estimates"="divf"`;
- binary64 emits `"reciprocal-estimates"="divd"`;
- a function using both emits `"reciprocal-estimates"="divf,divd"`.

That function attribute enables LLVM's target-lowering reciprocal-estimate hook;
the per-instruction fast-math flags remain the legality gate. This distinction
matters: `arcp` permits division/reciprocal algebra, while LLVM's
`reciprocal-estimates` setting controls whether code generation should override
the target default and attempt an estimate/refinement sequence.

```basic
DIM a!, b!, r!
r! = a! / b!
```

`FpFastMath` also consumes the same reciprocal legality directly for repeated
divisions by the same SSA divisor: [O0345](O0345-common-denominator-factoring.md)
creates one `1/d` and replaces the other divisions by multiplications.

## Target-selected refinement

The IR intentionally does not hard-code "one Newton step for SINGLE, two for
DOUBLE". Estimate accuracy, denormal behavior, available reciprocal instructions
and the profitable refinement count are target properties. LLVM's
`TargetLowering::getRecipEstimate` hook selects the estimate and reports the
required Newton-Raphson refinement count for the selected ISA.

The emitter deliberately does **not** request estimates for `x86_fp80`. The
16-bit/x87 route has no reciprocal-estimate instruction, and replacing `FDIV`
with a software Newton sequence is not a general win. Ordinary optimization also
carries neither the SPEED fast-math contract nor the function attribute, so exact
division remains required there.

## References

- [LLVM Language Reference — fast-math `arcp`](https://llvm.org/docs/LangRef.html#fast-math-flags)
- [LLVM `TargetLowering` reciprocal-estimate hooks](https://llvm.org/docs/doxygen/classllvm_1_1TargetLowering.html)
- [LLVM `TargetLoweringBase` reciprocal-estimate function attribute handling](https://llvm.org/docs/doxygen/TargetLoweringBase_8cpp_source.html)
