# O0338 — Reciprocal reuse across repeated divisions

| | |
|---|---|
| **Status** | 🟡 Partial — exact strict reciprocals plus `arcp`-authorized reuse across dominated call-free CFG regions |
| **Stage** | Mid-end |
| **Source** | `Ir/Passes/ReciprocalSequenceReuse.cs` |
| **Gate** | `--optimize`; general reciprocal reuse additionally requires `$OPTIMIZE SPEED` / `-OZF` or an explicit `AllowReciprocal` flag |
| **Verified by** | `ArithmeticIdiomOptimizationTests` |
| **Related** | [O0028](O0028-loop-invariant-code-motion.md), [O0341](O0341-reciprocal-approximation.md), [O0345](O0345-common-denominator-factoring.md) |

## The idea

Dividing repeatedly by the **same invariant** value can compute or reuse a
reciprocal and multiply instead. On x87, `FDIV` is substantially slower than
`FMUL`.

## What is implemented

Strict floating point keeps the exact case from v1: repeated F32/F64 division
by the same finite nonzero power-of-two constant whose reciprocal is also
representable and finite. The pass substitutes that exact reciprocal constant
directly, so no relaxed numerical contract is needed.

When a division carries `IrFastMathFlags.AllowReciprocal`, O0338 can also reuse
a runtime `1/d` for non-power-of-two constants and dynamic SSA divisors. One
eligible division must dominate the others that reuse its reciprocal, and every
CFG path between them must be call-free. The reciprocal is materialized at the
first dominating division rather than speculated into an earlier block. If that
division is already `1/d`, it is retained as the shared reciprocal.

Bit-identical F32/F64 constants are grouped even when lowering produced separate
`IrConstantFloat` objects. Dynamic divisors require identical SSA value identity.
Generated multiplies retain the arithmetic fast-math freedoms from their source
divisions, but do not inherit `arcp` or approximate-function flags.

O0345 performs the same-block common-denominator case earlier in the SPEED
pipeline. O0338 runs after LICM and extends reuse across blocks where dominance
proves that sharing does not introduce a speculative reciprocal evaluation.

## Applies to

Strict exact case:

```basic
a! = x! / 8!
b! = y! / 8!
```

where the IEEE reciprocal `0.125` is exact.

With `$OPTIMIZE SPEED`, a dominated sequence may additionally become:

```text
x/d; ...; y/d  ->  r = 1/d; x*r; ...; y*r
```

provided no call lies on a path from the first reused division to the later one.
Sibling branches are deliberately left alone because neither division dominates
the other.

## Numerical contract

LLVM's `arcp` fast-math flag explicitly permits treating `a / b` as
`a * (1.0 / b)`. PB-Compiler represents the same permission as
`IrFastMathFlags.AllowReciprocal`; ordinary optimized code does not receive it.
The strict power-of-two path remains bit-exact and does not depend on `arcp`.

## Still planned

- Guarded/lazy hoisting into a loop preheader so sibling or zero-trip loop paths
  can share a reciprocal without introducing an unconditional evaluation.
- Cost-model decisions for targets where reciprocal formation or register
  pressure changes the trade.
- Extended/x87 formats once exact representability is defined in the IR.
