# O0346 — Floating-point classification simplification

| | |
|---|---|
| **Status** | ✅ Implemented — proof-driven ordered comparisons fold from NaN/sign/finiteness and finite range facts |
| **Stage** | IR middle end |
| **Gate** | Ordinary optimizer; SPEED may add its explicit relaxed-FP assumptions |
| **IR** | `FpSimplify` + `FpDomainAnalysis` consuming `IrRangeAnalysis` |
| **Related** | [O0016](O0016-value-fact-analysis.md), [O0012](O0012-float-demotion.md), [C0003](C0003-x87-scheduling.md) |

## What is implemented

`FpSimplify` decides every ordered comparison form (`=`, `<>`, `<`, `<=`, `>`,
`>=`) when the answer follows from proven floating-point facts. That includes:

- ordered self-comparisons once NaN is excluded;
- comparisons with zero from positive, negative, non-negative, non-positive and
  non-zero facts;
- comparisons with `NaN` and, for values proven finite, with either infinity;
- comparisons between finite F32/F64 domains when their intervals prove an
  ordering, disjointness, or the same singleton value; and
- branch-refined integer ranges carried through integer-to-float conversion and
  supported affine/binary floating expressions.

Exact facts come from constants, integer-to-float conversions, widening casts,
conservative narrowing-cast facts, squares, and square roots whose arguments are
known to be inside the defined non-negative domain. Signed integer constants are
interpreted from their declared bit width rather than from the host `long` that
stores the IR bit pattern.

The branch/value facts come from the existing `IrRangeAnalysis`. The small
`FpDomainAnalysis` layer adapts those facts through integer-to-float conversion
and supported F32/F64 arithmetic; it does not introduce a competing CFG range
lattice.

## Strictness

Strict optimization acts only on classifications it can prove. A square of an
arbitrary float, for example, is not folded to non-negative because the input may
be NaN. Under SPEED, the explicit `NoNaNs`/`NoInfs` assumptions can strengthen
the same queries.

The domain evaluator walks supported F32/F64 expressions node-by-node and
preserves each declared rounding point. It refuses extended x87 arithmetic
rather than approximating an 80-bit intermediate with host `double`; F80 still
benefits from representation-independent facts such as the sign and finiteness
of an integer conversion.

## Conservative boundary

This is deliberately not a full IEEE abstract interpreter. Arbitrary float
inputs, float CFG joins, subnormal categories and exponent bounds remain unknown
unless another proof supplies the needed fact. Signed zero is handled correctly
by ordered comparison semantics but is not tracked as a separate lattice class.
Unknown classifications remain in the IR unchanged.
