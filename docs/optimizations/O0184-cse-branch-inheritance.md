# O0184 — CSE inheritance into dominated branches

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR middle end |
| **Source** | `Ir/Passes/Gvn.cs` — dominator-scoped value table; loads keyed by their Memory SSA version (`Ir/Analysis/IrMemorySsa.cs`) |
| **Gate** | `--optimize` |
| **Split from** | [O0003](O0003-common-subexpression-elimination.md) |

## What it is

A value computed before an `IF`/`SELECT` is reused in the arms, which the block
before the branch dominates. `Gvn` walks the dominator tree with a scoped value
table, so the recomputation inside the arm is replaced by the earlier SSA value
instead of being evaluated again.

The `y*320+x` "compute, then reuse inside a branch" pattern is the corpus's most
common shape for this.

## Sample

```basic
DIM x%, y%, o%
o% = y% * 320 + x%
IF flag% THEN o% = y% * 320 + x% + 1
```

## With the optimizer

```asm
    ...                      ; define the CSE slot before the IF
    mov     [bp-6], ax
    ...
    mov     ax, [bp-6]       ; inside the arm: reload, not recompute
    inc     ax
```

The listing shows the shape; where the reused value lives (a register or a
frame slot) is now the register allocator's decision.

## Why it is safe

Two instructions are congruent only when they apply the same operation to the
same SSA operands, and a load only when Memory SSA shows both reads see the same
memory version — so nothing between the two can have written the inputs. The
leader dominates the replaced occurrence, so it has already run on every path
that reaches the arm. Stores and calls with side effects are never numbered.
