# O0048 — IR: dead-store elimination

| | |
|---|---|
| **Status** | ✅ Implemented (intra-block) |
| **Stage** | IR mid-end, after GVN and load/store forwarding |
| **Source** | `Ir/Passes/DeadStoreElim.cs` |
| **Related** | [O0002](O0002-dead-code-elimination.md), [O0047](O0047-ir-redundant-memory.md), [O0050](O0050-ir-dce.md) |

## What it is

A store is dead when a **later store in the same block writes the same address
before any load that could observe it**, and nothing in between could read
memory. The earlier store then contributes nothing and is removed.

## Sample

```basic
DIM a%(0 TO 9)
a%(0) = 1
a%(0) = 2
PRINT a%(0)
```

## Before

```llvm
  %p = getelementptr [10 x i16], ptr @a, i16 0, i16 0
  store i16 1, ptr %p        ; overwritten before any load
  store i16 2, ptr %p
  %0 = load i16, ptr %p
```

## After

```llvm
  %p = getelementptr [10 x i16], ptr @a, i16 0, i16 0
  store i16 2, ptr %p
  %0 = load i16, ptr %p
```

## Equivalent BASIC

```basic
DIM a%(0 TO 9)
a%(0) = 2
PRINT a%(0)
```

## Why it is safe

The two alias questions are asked in opposite directions, which is what makes
the pass sound:

- a load keeps a pending store alive if it **may** alias it;
- the overwriting store kills the pending one only if it **definitely** aliases
  (the same SSA pointer — same base and offset).

Any intervening **call** invalidates every pending store, since a callee could
read the memory. Addresses are canonical SSA values by the time this runs
(after GVN), so the pointer identity test is meaningful.
