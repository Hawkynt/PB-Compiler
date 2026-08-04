# O0003 — Common-subexpression elimination

| | |
|---|---|
| **Status** | ✅ Implemented (block-local, cross-branch, past merges, through loop preheaders, plus redundant array loads) |
| **Stage** | Pre-emission analysis + emitter |
| **IR** | ✅ `Ir/Passes/Gvn.cs` - and GLOBAL where the emitter's is block-local, so a subexpression shared across two blocks is still computed once; verified by `PortedMidEndOptimizationsTests` |
| **Source** | `CodeGen/OptCommonSubexpr.cs`, `CodeGen/CodeGenerator.Expressions.cs` |
| **Gate** | `--optimize`; modular-int16 caching disabled under `$ERROR NUMERIC/OVERFLOW/ALL` |
| **Verified by** | `tests/diff/DIFF33.BAS`, `DIFF67.BAS` (past merge), `DIFF68.BAS` (`SELECT`), `DIFF69.BAS` (array loads) |
| **Related** | [O0028](O0028-loop-invariant-code-motion.md), [O0034](O0034-redundant-load-elimination.md), [O0046](O0046-ir-gvn.md) |
| **Split into** | [O0184](O0184-cse-branch-inheritance.md), [O0185](O0185-cse-past-merge.md), [O0186](O0186-cse-loop-preheader.md), [O0187](O0187-redundant-array-load.md), [O0188](O0188-cse-if-condition.md) |

## What it is

A pre-pass marks pure integer subexpression trees that recur in a straight-line
run. The first occurrence computes into a reserved frame slot; every later
occurrence reloads that slot instead of recomputing the tree.

Two emission contexts are cached separately, because they are emitted by
different paths: genuinely integer-typed trees (LONG/DWORD/comparison) through
the normal emitter, and the SINGLE-promoted **modular int16** trees — the
`y * 320 + x` graphics case computed on the 16-bit ALU — through
`EmitModularInt16`.

**This page covers the block-local case.** The cache's reach beyond one basic
block is a set of separate entries (see *Split into* above): inheritance into
branches, retention past a merge, reuse through loop preheaders, array-element
load caching, and registering the `IF` condition itself.

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

A reload only ever follows a define from **identical inputs** with no
intervening write or barrier, so any `$ERROR` trap the define would raise fires
exactly where the un-CSE'd first occurrence would have. Every call, branch,
label, loop, `POKE` or inline-asm statement clears the cache; a scalar write
invalidates the slots that read it; a write to any element of a cached array
invalidates that array's entries (a write to a *different* array does not).

A constant-foldable subtree is never a CSE candidate: the emitter folds the
defining occurrence to a literal and emits nothing for it, which would leave the
second occurrence reloading a slot nothing ever wrote.

## Limits

Arbitrary CFG value numbering lives in the IR mid-end
([O0046](O0046-ir-gvn.md)); hoisting *loads* out of loops needs memory SSA
([O0060](O0060-memory-ssa.md)).
