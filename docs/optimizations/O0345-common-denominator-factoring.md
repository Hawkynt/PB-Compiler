# O0345 — Common-denominator factoring

| | |
|---|---|
| **Status** | ✅ Implemented for repeated divisions by the identical SSA divisor within a call-free block region |
| **Stage** | IR middle end |
| **Gate** | Optimizer + `$OPTIMIZE SPEED` / `-OZF` |
| **IR** | `FpFastMath.FactorCommonDenominators` |
| **Verified by** | `O0345CommonDenominatorFactoringTests`, `FpOptimizationTests` |
| **Related** | [O0338](O0338-reciprocal-sequence-reuse.md), [O0003](O0003-common-subexpression-elimination.md), [O0344](O0344-fp-reassociation.md) |

## What is implemented

Two or more `FDiv` operations using the same SSA divisor become one reciprocal
and multiplications:

```text
x/d, y/d  ->  r = 1/d; x*r, y*r
```

The grouping uses value identity, so an apparently equal expression that was
recomputed is not assumed equal here; GVN may establish that identity earlier.
A call flushes the current groups rather than assuming a callee cannot affect a
value's provenance.

If the first division in a group is already `1/d`, that dominating value is retained
as the shared reciprocal rather than creating another division. Any later `1/d`
is replaced directly by the shared reciprocal instead of materializing a redundant
`1*r`. `arcp` belongs on the reciprocal division; generated multiplications receive
arithmetic fast-math flags but not reciprocal/approx-function flags.

## Numerical contract

`x/d` and `x*(1/d)` can differ in the last bits, so this is SPEED-only. The
strict pipeline leaves each division in place. LLVM's `arcp` fast-math contract
explicitly permits the corresponding `a / b` to `a * (1.0 / b)` rewrite.

This implementation deliberately chooses the shared-reciprocal form rather than
rewriting `(a/d)+(b/d)` into `(a+b)/d`; the former saves divisions without also
changing the addition tree.
