# O0043 — IR: instruction combining

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR mid-end |
| **Source** | `Ir/Passes/InstCombine.cs` |
| **Related** | [O0001](O0001-constant-folding.md), [O0050](O0050-ir-dce.md), [O0061](O0061-reassociation.md) |

## What it is

Peephole simplification on the IR: constant folding plus the standard algebraic
identities — `x + 0`, `x * 1`, `x * 0`, `x AND -1`, `x XOR x`, `x = x`, and so
on. A simplified instruction is replaced by its value everywhere (RAUW) and left
for [dead-code elimination](O0050-ir-dce.md) to remove. It runs to a **fixpoint**,
so a simplification that exposes another is taken too.

## Sample

```basic
DIM a%, b%
b% = (a% + 0) * 1
PRINT b% - b%
```

## Before

```llvm
  %0 = add i16 %a, 0
  %1 = mul i16 %0, 1
  %2 = sub i16 %1, %1
  call void @rt_print_i16(i16 %2)
```

## After

```llvm
  call void @rt_print_i16(i16 0)
```

`%0` folds to `%a`, `%1` to `%a`, and `%a - %a` to the constant 0; all three
instructions become unused and DCE sweeps them.

## Equivalent BASIC

```basic
PRINT 0
```

## Why it is safe

Each rewrite is an identity of the IR's own integer semantics (two's complement,
wrapping), applied only to instructions whose operands are already SSA values —
so replacing the result everywhere is exactly substituting equals for equals.
Instructions with side effects are not candidates. A strength-reduced
replacement that needs a new instruction is inserted before the one it replaces,
keeping dominance intact.
