# O0003 — Common-subexpression elimination

| | |
|---|---|
| **Status** | ✅ Implemented (block-local, cross-branch, past merges, through loop preheaders, plus redundant array loads) |
| **Stage** | IR middle end |
| **Source** | `Ir/Passes/Gvn.cs` — `Run`, `KeyOf`; loads numbered through `Ir/Analysis/IrMemorySsa.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `PortedMidEndOptimizationsTests`, `tests/diff/DIFF33.BAS`, `DIFF67.BAS` (past merge), `DIFF68.BAS` (`SELECT`), `DIFF69.BAS` (array loads) |
| **Related** | [O0028](O0028-loop-invariant-code-motion.md), [O0034](O0034-redundant-load-elimination.md), [O0046](O0046-ir-gvn.md) |
| **Split into** | [O0184](O0184-cse-branch-inheritance.md), [O0185](O0185-cse-past-merge.md), [O0186](O0186-cse-loop-preheader.md), [O0187](O0187-redundant-array-load.md), [O0188](O0188-cse-if-condition.md) |

## What it is

`Gvn` walks the dominator tree with a scoped hash table. Two binary
operations, comparisons, casts or address computations with the same opcode and
the same operands (commutative operands ordered, so `a+b` equals `b+a`) are the
same value, and the one dominated by the other is replaced by it. A load is
numbered together with the memory version Memory SSA says it reads, so two
reads of an unchanged cell are one; a deterministic runtime call that only
reads memory (`LEN` of a descriptor) is numbered the same way. The surviving
value is an ordinary SSA value; the register allocator decides whether it stays
in a register or is spilled to a frame slot.

**This page covers the straight-line case.** The same dominator-scoped table
also covers the cases listed separately under *Split into* above: inheritance
into branches, retention past a merge, reuse through loop preheaders,
array-element load reuse, and the `IF` condition itself.

## Sample

```basic
DIM x%, y%, o%, p%
x% = 10 : y% = 20
o% = y% * 320 + x%
p% = y% * 320 + x% + 1
```

## Without the optimizer

`y * 320 + x` is computed twice, multiply and all:

```asm
    mov     ax, [y]
    mov     bx, 0140h
    imul    bx               ; y*320
    add     ax, [x]
    mov     [o], ax
    mov     ax, [y]          ; the whole tree again
    mov     bx, 0140h
    imul    bx
    add     ax, [x]
    inc     ax
    mov     [p], ax
```

## With the optimizer

```asm
    mov     ax, [y]
    shl     ax, 1            ; *320 strength-reduced (O0004)
    ...
    add     ax, [x]
    mov     [bp-6], ax       ; CSE slot: DEFINE
    mov     [o], ax
    mov     ax, [bp-6]       ; CSE slot: RELOAD
    inc     ax
    mov     [p], ax
```

## Equivalent BASIC

```basic
DIM x%, y%, o%, p%, t%
x% = 10 : y% = 20
t% = y% * 320 + x%
o% = t%
p% = t% + 1
```

## Why it is safe

A value is only ever replaced by an identical computation that **dominates**
it, so the leader has already run on every path that reaches the replaced one,
and any trap it could raise fired there first. Stores, allocas, phis and calls
with other effects are never numbered. A load is only reused when Memory SSA
gives both reads the same clobbering memory version, so any intervening write
that may alias the cell — including a call that may write memory — separates
them.

## Limits

Only fully redundant values are removed: a computation repeated on one path
but not on another (partial redundancy) stays. Hoisting invariant work out of
loops is `Licm` ([O0028](O0028-loop-invariant-code-motion.md)); the memory
versions come from [O0060](O0060-memory-ssa.md).
