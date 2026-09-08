# O0348 — x87 stack scheduling

| | |
|---|---|
| **Status** | ✅ Implemented — pressure-aware expression-tree ordering, depth-proven stackification and FXCH repair |
| **Stage** | Machine IR, after selection and before ordinary scheduling/allocation |
| **Gate** | Optimizer (`MachineOptimizationState`) |
| **Source** | `Backend/X87StackOptimizer.cs`, invoked by `MachineScheduler` for optimizer-marked functions |
| **Related** | [C0003](C0003-x87-scheduling.md), [O0349](O0349-x87-value-retention.md), [O0013](O0013-promotion-lowering.md) |

## What is implemented

The selector intentionally starts from an empty-x87-stack form: each floating
SSA result is stored in a private TBYTE frame slot and reloaded when consumed.
`X87StackOptimizer` recognizes the selected shape for `left op right`, removes
private spill/reload pairs, and profiles both adjacent x87 subtrees.

For a subtree with peak depth `P`, keeping the other operand resident costs one
additional stack register. The scheduler therefore compares

```text
left first:  max(leftPeak,  1 + rightPeak)
right first: max(rightPeak, 1 + leftPeak)
```

and chooses right-first evaluation only when it strictly lowers the measured
peak and remains within the architectural eight-register x87 stack.

```basic
DIM a!, b!, c!, d!, r!
r! = a! + ((b! + c!) * d!)
```

A heavier right subtree can therefore execute before a cheap left subtree
instead of forcing the left value to spill while the right side reaches its
peak.

## Operand order and `FXCH`

After right-first evaluation the stack contains `ST(0)=left`, `ST(1)=right`.
That is already sufficient for commutative `FADDP`/`FMULP`. For
`FSUBP`/`FDIVP`, however, the root needs `ST(1)=left`, `ST(0)=right`, so the
scheduler inserts one `FXCH` immediately before the popping operation.

This changes evaluation order, not expression structure: the same operations
and parenthesization are retained.

## Safety boundaries

Reordering is intentionally stricter than ordinary value retention. Both
subtrees must be contiguous, fully modelled x87 code and may contain memory
reads but no writes. Calls, inline assembly, terminators, physical clobbers,
unknown x87 stack effects and required stores stop scheduling.

SINGLE/DOUBLE spill/reload pairs are never removed because those stores are
semantic rounding points. Private TBYTE temporaries remain eligible because
materializing and reloading them preserves the extended-precision value.

If neither subtree order fits within eight x87 registers, the parent spill is
kept.

## Reference model

The implementation is independently derived from the architectural x87 stack
model and the Ershov/Sethi-Ullman rule of evaluating the higher-pressure
subtree first. Intel documents the x87 register file as an eight-entry stack
and `FXCH` as exchanging `ST(0)` with another stack register. LLVM's x87
stackifier was consulted only as a behavioral/architectural reference; no
implementation code was copied.
