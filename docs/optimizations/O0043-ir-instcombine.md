# O0043 — IR: instruction combining

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR mid-end |
| **Source** | `Ir/Passes/InstCombine.cs` |
| **Related** | [O0001](O0001-constant-folding.md), [O0031](O0031-branch-fusion.md), [O0050](O0050-ir-dce.md), [O0061](O0061-reassociation.md) |

## What it is

Peephole simplification on the IR: constant folding plus the standard algebraic
identities — `x + 0`, `x * 1`, `x * 0`, `x AND -1`, `x XOR x`, `x = x`, and so
on. A simplified instruction is replaced by its value everywhere (RAUW) and left
for [dead-code elimination](O0050-ir-dce.md) to remove. It runs to a **fixpoint**,
so a simplification that exposes another is taken too.

Boolean comparisons are canonicalized as values too. Since an `i1` has only the
patterns false and true:

- `b = TRUE` and `b <> FALSE` become `b`;
- `b = FALSE` and `b <> TRUE` become logical `NOT b`;
- when `b` is itself an **integer** comparison, logical NOT is represented by the
  complementary predicate (`x < y` → `x >= y`) rather than materializing an XOR.
  That keeps the comparison shape visible to [branch fusion](O0031-branch-fusion.md).

The existing widened-Boolean rule uses the same path, so
`(SEXT i1 (x < y)) = 0` becomes the complementary integer comparison directly.

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

For a direct Boolean comparison:

```llvm
  %lt = icmp slt i16 %x, 10
  %not = icmp eq i1 %lt, false
```

canonicalization produces:

```llvm
  %not = icmp sge i16 %x, 10
```

with no intermediate truth-value operation.

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

The Boolean comparison rules depend only on the two possible `i1` bit patterns.
Complementary **integer** comparison predicates are exhaustive because integer
ordering has no unordered state. Ordered floating comparisons are intentionally
not inverted this way: with NaN, `NOT (x < y)` is true while the ordered
`x >= y` predicate is false. Floating comparison negation therefore remains a
Boolean XOR, which preserves the original NaN behavior exactly.
