# O0346 — Floating-point classification simplification

| | |
|---|---|
| **Status** | 🟨 Partial — proven NaN/sign/finiteness facts and integer-derived ranges are implemented |
| **Stage** | IR middle end |
| **Gate** | Ordinary optimizer; SPEED may add its explicit relaxed-FP assumptions |
| **IR** | `FpSimplify` + `FpDomainAnalysis` consuming `IrRangeAnalysis` |
| **Related** | [O0016](O0016-value-fact-analysis.md), [O0012](O0012-float-demotion.md), [C0003](C0003-x87-scheduling.md) |

## What is implemented

`FpSimplify` decides ordered self-comparisons and comparisons with zero when the
result follows from proven value facts. Sources include finite integer-to-float
conversions, branch-refined integer ranges, exact constants, widening casts,
conservative narrowing-cast facts, squares, and square roots whose arguments are
known to be inside the defined non-negative domain.

The branch/value facts come from the existing `IrRangeAnalysis`. The small
`FpDomainAnalysis` layer adapts those facts through integer-to-float conversion
and supported affine FP expressions; it does not duplicate CFG range analysis.

## Strictness

Strict optimization does not let algebraically collapsed endpoints prove that
all intermediate operations remained finite or nonzero. The domain evaluator
walks supported F32/F64 expressions node-by-node and preserves each declared
rounding point. Extended x87 precision is declined rather than approximated with
a host `double`.

Under SPEED, explicit relaxed-FP assumptions can strengthen the same queries.

## Remaining scope

A full IEEE abstract domain for arbitrary float inputs, CFG joins, signed zero,
subnormal categories and exponent bounds remains future work. The current pass
folds only classifications it can prove with the implemented facts.
