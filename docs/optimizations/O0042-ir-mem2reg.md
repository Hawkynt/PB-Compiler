# O0042 — IR: mem2reg (stack-slot promotion)

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR mid-end (`--emit-c` / `--emit-llvm`), first pass of the standard pipeline |
| **Source** | `Ir/Passes/Mem2Reg.cs` |
| **Related** | [O0017](O0017-sccp.md) (the AST-tier SSA), [O0043](O0043-ir-instcombine.md), [O0046](O0046-ir-gvn.md) |

## What it is

The IR lowering emits every variable as an `alloca` with loads and stores —
trivially correct, and opaque to every value-based analysis. `mem2reg` replaces
an alloca whose only uses are direct loads and stores with values flowing
through phi nodes placed at the **iterated dominance frontier** of its stores
(the classic Cytron construction).

This is the pass that turns the lowering's memory form into real SSA, which is
what SCCP, GVN and instcombine need to do anything at all.

## Sample

```basic
DIM x%, y%
x% = 1
IF y% > 0 THEN x% = 2
PRINT x%
```

## Before

```llvm
  %x = alloca i16
  store i16 1, i16* %x
  br i1 %c, label %then, label %join
then:
  store i16 2, i16* %x
  br label %join
join:
  %0 = load i16, i16* %x
  call void @rt_print_i16(i16 %0)
```

## After

```llvm
  br i1 %c, label %then, label %join
then:
  br label %join
join:
  %x.0 = phi i16 [ 1, %entry ], [ 2, %then ]
  call void @rt_print_i16(i16 %x.0)
```

## Equivalent BASIC

Unchanged — the variable simply no longer needs a memory cell.

## Why it is safe

Only allocas whose uses are *all* direct loads and stores are promoted; anything
whose address escapes (passed BYREF, cast, stored) stays in memory. PB
zero-initializes variables, so a slot with **no reaching store** reads as the
zero constant of its type — never `undef`, which is what makes the promotion
correct without an explicit initializer.
