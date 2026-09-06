# O0348 — x87 stack scheduling

| | |
|---|---|
| **Status** | 🟨 Partial — depth-proven expression-tree stackification implemented; arbitrary subtree reordering/FXCH synthesis remains future work |
| **Stage** | Machine IR, after selection and before ordinary scheduling/allocation |
| **Gate** | Optimizer (`MachineOptimizationState`) |
| **Source** | `Backend/X87StackOptimizer.cs`, invoked by `MachineScheduler` for optimizer-marked functions |
| **Related** | [C0003](C0003-x87-scheduling.md), [O0349](O0349-x87-value-retention.md), [O0013](O0013-promotion-lowering.md) |

## What is implemented

The selector intentionally starts from an empty-x87-stack form: each floating
SSA result is stored in a private TBYTE frame slot and reloaded when consumed.
`X87StackOptimizer` recognizes the selected shape for `left op right` and keeps
the completed left subtree resident while evaluating the right subtree.

Before doing so it simulates the right subtree's x87 stack effect. With the
retained left value occupying one register, the maximum depth must stay within
the architectural eight-register stack. Calls, inline assembly, terminators,
physical clobbers and unmodelled x87 stack operations stop the transform.

```basic
DIM a!, b!, c!, d!, r!
r! = (a! + b!) * (c! + d!)
```

For the ordinary selected tree this removes the intermediate TBYTE stores and
reloads while leaving the arithmetic operation order unchanged.

## Exactness

This is distinct from [O0344](O0344-fp-reassociation.md). O0348 does not change
which floating operations are performed or their parenthesization; it changes
where an intermediate lives. SINGLE/DOUBLE spill/reload pairs are not removed,
because those stores are required rounding points.

## Remaining scope

The pass does not yet choose between alternate evaluation orders or synthesize
`FXCH` to realize a lower-Ershov-number ordering. It therefore implements the
safe stackification/value-placement subset rather than claiming a complete x87
expression scheduler.
