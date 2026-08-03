# O0045 — IR: correlated value propagation

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR mid-end |
| **Source** | `Ir/Passes/CorrelatedValueProp.cs` |
| **Related** | [O0044](O0044-ir-sccp.md), [O0016](O0016-value-fact-analysis.md) |

## What it is

Facts learned from a branch are propagated into the code the branch guards. When
a block ends in `condbr (icmp eq x, C), T, F` and the true successor `T` is
entered **only** through that edge, then `x = C` throughout the region `T`
dominates — so every non-phi use of `x` in that region is replaced by the
constant `C`, which then folds.

## Sample

```basic
DIM k%, r%
IF k% = 3 THEN
  r% = k% * 100
  PRINT r%
END IF
```

## Before

```llvm
  %0 = icmp eq i16 %k, 3
  br i1 %0, label %then, label %join
then:
  %1 = mul i16 %k, 100        ; %k is 3 here, but nothing says so
  call void @rt_print_i16(i16 %1)
```

## After

```llvm
  %0 = icmp eq i16 %k, 3
  br i1 %0, label %then, label %join
then:
  call void @rt_print_i16(i16 300)
```

## Equivalent BASIC

```basic
IF k% = 3 THEN PRINT 300
```

## Why it is safe

The substitution is applied only inside the region **dominated** by the true
edge, and only when that edge is the sole way in — otherwise a path could reach
the code with a different `x`. Phi nodes are excluded because their operands
belong to the predecessor edges, not to the block. Since `x` is an SSA value, it
cannot be reassigned within the region.
