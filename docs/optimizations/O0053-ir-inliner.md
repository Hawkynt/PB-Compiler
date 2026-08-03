# O0053 — IR: function inlining

| | |
|---|---|
| **Status** | ✅ Implemented (direct calls, non-recursive callees, size budget 64 instructions) |
| **Stage** | IR mid-end (module level) |
| **Source** | `Ir/Passes/Inliner.cs`, `Ir/IrCloner.cs` |
| **Related** | [O0006](O0006-inlining.md) (the x86 tier), [O0054](O0054-ir-global-dce.md) |

## What it is

A direct call to a non-recursive defined callee within the size budget is
replaced by the callee's body:

1. the callee's blocks are cloned into the caller (`IrCloner`);
2. parameters are mapped to the call arguments;
3. the call site's block is **split**, so the code after the call becomes a
   continuation block;
4. each cloned `ret` branches to that continuation;
5. the call's result is the single returned value, or a phi over the returns.

Beyond removing the call overhead, this exposes the callee body to the caller's
optimizer — which is usually the larger win, because the argument values are now
known.

## Sample

```basic
FUNCTION Square&(BYVAL v&)
  Square& = v& * v&
END FUNCTION

DIM r&
r& = Square&(7)
PRINT r&
```

## Before

```llvm
  %0 = call i32 @Square(i32 7)
  call void @rt_print_i32(i32 %0)
```

## After (inlined, then folded by instcombine)

```llvm
  call void @rt_print_i32(i32 49)
```

## Equivalent BASIC

```basic
PRINT 49
```

## Why it is safe

Only **direct** calls to **defined**, **non-recursive** callees are inlined, so
the clone terminates and the callee's body is fully known. The block split keeps
the CFG well-formed and the phi over multiple `ret`s preserves the value on each
path. Code growth is bounded by the 64-instruction callee budget.

Once the last caller is inlined, the now-unreferenced function is removed by
[global DCE](O0054-ir-global-dce.md).
