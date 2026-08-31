# O0350 — Overflow-check coalescing

| | |
|---|---|
| **Status** | ✅ Implemented (IR straight-line Error 6 chains) |
| **Stage** | Emitter |
| **IR** | `PowerBasic.Compiler/Ir/Passes/OverflowCheckCoalescing.cs` |
| **Related** | [O0219](O0219-overflow-check-elimination.md), [O0308](O0308-speculative-overflow-elimination.md), [O0117](O0117-bounds-check-merging.md) |

## The idea

A chain of checked operations emits a `JNO` after each one. When the operations
share operands, **one** range guard on the inputs can replace the whole chain:
prove the inputs small enough and no intermediate can overflow.

## Applies to

```basic
$ERROR OVERFLOW ON
DIM a%, b%, c%, r%
r% = a% + b% + c%            ' two adds, two JNO guards
```

If `a%`, `b%` and `c%` are each within ±10 000, neither sum can overflow, and one
test on the widest input replaces both guards.

## IR implementation

The target-neutral lowering does not carry an x86 flags register. A checked operation instead ends in
`condbr overflow, error6, next`. `RangeCheckElim` already removes a guard when interval facts prove it
impossible; O0350 handles the remaining straight-line case by delaying the first branch across only
pure, non-trapping IR and testing `overflow1 OR overflow2` at the next Error 6 guard. Repeating that
rewrite coalesces longer chains.

This is intentionally narrower than a generic trap merger. Loads, stores, calls, divisions and other
observable or trapping instructions break the chain. Functions with `ON ERROR` handlers are excluded
by the pass manager, so the transform never changes a recoverable `RESUME` point. The abandoned Error
6 block is then ordinary unreachable CFG for `simplifycfg`/DCE to collect.

## What it needs

- The interval arithmetic of [O0016](O0016-value-fact-analysis.md) applied to
  the *precondition* rather than to each operation.
- **Which** operation would have overflowed first must still be preserved when the error is recoverable:
  under `$ERROR OVERFLOW` an armed `ON ERROR` handler can observe the fault point. Such functions are
  deliberately outside the IR optimizer because their exceptional edges are not represented in the CFG;
  the coalescing pass therefore only runs where Error 6 terminates execution.
