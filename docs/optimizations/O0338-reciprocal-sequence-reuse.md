# O0338 — Reciprocal reuse across repeated divisions

| | |
|---|---|
| **Status** | 🟡 Partial — repeated IEEE divisions by the same exact power-of-two constant become multiplies by its exact reciprocal |
| **Stage** | Mid-end |
| **Source** | `Ir/Passes/ReciprocalSequenceReuse.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `ArithmeticIdiomOptimizationTests` |
| **Related** | [O0028](O0028-loop-invariant-code-motion.md), [O0341](O0341-reciprocal-approximation.md), [O0345](O0345-common-denominator-factoring.md) |

## The idea

Dividing repeatedly by the **same invariant** value can compute or reuse a
reciprocal and multiply instead. On x87, `FDIV` is substantially slower than
`FMUL`.

## Implemented v1

Without a fast-math contract, `x / d` and `x * (1/d)` are not generally
bit-identical. `ReciprocalSequenceReuse` therefore takes only the exact case:
repeated F32/F64 division by the same finite nonzero power-of-two constant whose
reciprocal is also representable and finite.

The pass requires more than one matching division and substitutes the exact
reciprocal constant directly. Non-power-of-two divisors such as `3.0` stay as
divisions under strict FP rules.

## Applies to

```basic
a! = x! / 8!
b! = y! / 8!
```

where the IEEE reciprocal `0.125` is exact.

## Still planned

- General loop-invariant divisors behind an explicit reciprocal/fast-math
  contract.
- Hoisting one guarded reciprocal into a loop preheader while preserving the
  first division's zero/exception behaviour.
- Cost-model decisions for targets where reciprocal formation or register
  pressure changes the trade.
- Extended/x87 formats once exact representability is defined in the IR.
