# O0050 — IR: dead-code elimination

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR mid-end |
| **Source** | `Ir/Passes/Dce.cs` |
| **Related** | [O0043](O0043-ir-instcombine.md), [O0042](O0042-ir-mem2reg.md), [O0054](O0054-ir-global-dce.md) |

## What it is

An instruction with **no users and no side effects** computes nothing anyone
needs, so it is removed. Removal cascades: an operand that loses its last user
becomes dead in turn.

This is the sweeper that cleans up after instcombine and mem2reg, which
deliberately leave replaced instructions in place (they RAUW the value and move
on).

## Sample

```basic
DIM a%, b%
b% = a% * 2
PRINT a%
```

## Before

```llvm
  %0 = mul i16 %a, 2         ; nothing reads %0 once the store is promoted away
  call void @rt_print_i16(i16 %a)
```

## After

```llvm
  call void @rt_print_i16(i16 %a)
```

## Equivalent BASIC

```basic
PRINT a%
```

## Why it is safe

Stores, calls and terminators are **never** removed: their effect or control
transfer is observable, regardless of whether anyone uses their result. Only
value-producing, side-effect-free instructions with an empty user list qualify,
which by definition cannot change what the program does.
