# O0344 — Floating-point reassociation

| | |
|---|---|
| **Status** | 🟡 Partial — local single-use `FAdd`/`FSub`/`FMul` trees are balanced; loop-reduction splitting remains target-gated work |
| **Stage** | IR middle end |
| **Gate** | Optimizer + `$OPTIMIZE SPEED` / `-OZF` |
| **IR** | `FpFastMath` with `IrFastMathFlags.Reassociate`; subtraction additionally requires `NoSignedZeros` |
| **Related** | [O0120](O0120-multiple-accumulators.md), [O0121](O0121-reduction-tree-balancing.md), [O0061](O0061-reassociation.md), [O0312](O0312-parallel-reduction.md) |

## What is implemented

`FpFastMath` recognizes serial same-block IEEE floating expression trees of 4–32
leaves. Internal nodes must be single-use so the rewrite never duplicates work;
a shared intermediate becomes an opaque leaf instead.

Pure `FAdd` and `FMul` chains consume the explicit `Reassociate` permission.
Mixed `FAdd`/`FSub` trees additionally require `NoSignedZeros`, matching the
stronger legality condition used for general floating reassociation. The tree is
rebuilt only when its current dependency depth exceeds the balanced depth.

```text
(((a+b)+c)+d)  ->  (a+b) + (c+d)
(((a+b)-c)+d)  ->  (a+b) - (c-d)
```

The mixed add/subtract form carries each flattened leaf with its algebraic sign,
then rebuilds signed subtrees without inventing extra negation operations. This
preserves the original left-to-right term sequence while reducing dependency
depth. Once the replacement is wired in, the superseded single-use tree is
removed immediately rather than leaving a dead serial chain for a later DCE
sweep.

Every generated arithmetic instruction carries only the floating permissions
applicable to arithmetic; reciprocal and approximate-function permissions do not
leak onto the new nodes.

## Why SPEED only

Floating addition and multiplication are not generally associative. Balancing
changes rounding points, and reassociating subtraction also changes observable
signed-zero behavior unless that distinction has explicitly been waived.
Ordinary optimization therefore cannot perform this rewrite. `$OPTIMIZE SPEED`
grants the full fast-math contract, including `reassoc` and `nsz`, and the LLVM
backend emits those permissions explicitly.

## Remaining scope

This pass balances expression trees that already exist. Turning a loop-carried
floating reduction into several accumulators or a parallel reduction tree is a
loop transform with target-cost, register-pressure, dependence and profitability
questions. The current x86-16 target does not profit from blindly multiplying
accumulators, so that work remains target-gated under [O0120](O0120-multiple-accumulators.md),
[O0121](O0121-reduction-tree-balancing.md) and [O0312](O0312-parallel-reduction.md).
