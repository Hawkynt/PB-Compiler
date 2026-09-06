# O0344 — Floating-point reassociation

| | |
|---|---|
| **Status** | 🟨 Partial — local single-use `FAdd`/`FMul` chains are balanced; loop-reduction restructuring remains separate work |
| **Stage** | IR middle end |
| **Gate** | Optimizer + `$OPTIMIZE SPEED` / `-OZF` |
| **IR** | `FpFastMath` with `IrFastMathFlags.Reassociate` |
| **Related** | [O0121](O0121-reduction-tree-balancing.md), [O0061](O0061-reassociation.md), [O0312](O0312-parallel-reduction.md) |

## What is implemented

`FpFastMath` recognizes serial same-block floating `FAdd` and `FMul` trees of
4–32 leaves. Internal nodes must be single-use so the rewrite does not duplicate
work. When the current depth is worse than a balanced tree, it rebuilds the tree
while preserving the original left-to-right leaf order.

```text
(((a+b)+c)+d)  ->  (a+b) + (c+d)
```

Every generated arithmetic instruction carries only the floating permissions
applicable to arithmetic; reciprocal/approx-function permissions do not leak
onto the new nodes.

## Why SPEED only

Floating addition and multiplication are not generally associative. Balancing
changes rounding points, so ordinary optimization cannot perform this rewrite.
`reassoc` is an explicit numerical permission, emitted to LLVM as such.

## Remaining scope

This pass balances expression trees that already exist. Turning a loop-carried
floating reduction into several accumulators or a parallel reduction tree is a
loop transform with additional dependence/profitability questions and remains
with [O0121](O0121-reduction-tree-balancing.md) / [O0312](O0312-parallel-reduction.md).
