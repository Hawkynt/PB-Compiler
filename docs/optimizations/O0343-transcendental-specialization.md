# O0343 — Transcendental function specialization

| | |
|---|---|
| **Status** | ✅ Implemented for range-driven LUT/polynomial specialization plus target `afn` fallback |
| **Stage** | IR middle end + target lowering |
| **Gate** | Optimizer + `$OPTIMIZE SPEED` / `-OZF` |
| **IR** | `FpDomainAnalysis`, `FpDomainSpecialization`, `FpFastMath` |
| **Related** | [O0337](O0337-polynomial-evaluation.md), [O0332](O0332-lookup-table-generation.md), [O0341](O0341-reciprocal-approximation.md) |

## What is implemented

O0343 consumes the same branch-refined `IrRangeAnalysis` facts already used for
bounds and overflow reasoning; there is no independent floating range engine.
`FpDomainAnalysis` adapts an integer SSA range through integer-to-float casts and
simple affine floating arithmetic.

SPEED then has three progressively more general choices:

1. **Finite discrete domain.** If the argument is provably derived from one
   integer SSA value with at most 256 possible values, the compiler evaluates
   the function at compile time and creates a typed FP lookup table when the
   backend advertises typed constant-table support. A narrow floating interval
   by itself is *not* enough — `[0,1]` still contains many floating values.
2. **Narrow continuous kernel.** Proven small intervals use independently
   derived Taylor/Horner kernels: `SIN`, `COS`, `ATN`, `EXP`, and `LOG` close to
   one have conservative kernel domains.
3. **General call.** Otherwise the call remains, but `FpFastMath` marks a known
   math intrinsic `afn`, allowing target-specific approximation.

The LLVM hosted path enables typed FP tables. Native x87 currently keeps the
same range-specialized polynomial path but leaves typed table generation off,
because its synthesized-global data-cell route does not yet materialize these
arrays.

## Numerical contract

The lookup evaluator uses `System.Math` only as a compile-time SPEED oracle. It
is not used by strict constant folding and is not claimed bit-identical to PBC's
x87/runtime transcendental implementation. The polynomial coefficients are
mathematical series constants independently derived from the functions rather
than copied from an implementation.

Ordinary optimization never runs `FpDomainSpecialization` and carries no `afn`
permission.
