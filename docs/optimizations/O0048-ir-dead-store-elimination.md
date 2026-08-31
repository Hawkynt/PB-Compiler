# O0048 — IR: dead-store elimination

| | |
|---|---|
| **Status** | ✅ Implemented (intra-block) |
| **Stage** | IR mid-end, after GVN and load/store forwarding |
| **Source** | `Ir/Passes/DeadStoreElim.cs` |
| **Related** | [O0002](O0002-dead-code-elimination.md), [O0047](O0047-ir-redundant-memory.md), [O0050](O0050-ir-dce.md), [O0171](O0171-alias-analysis.md) |

## What it is

A store is dead when a **later store in the same block completely covers its
byte range before any load that could observe it**, and nothing in between
could read memory. The earlier store then contributes nothing and is removed.

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

The two alias questions are intentionally different:

- a load keeps a pending store alive if its typed byte range **may alias** it;
- a later store kills a pending one only when the shared alias analysis can
  prove the later byte range **completely covers** every byte of the earlier
  store.

That width check matters with the IR's opaque pointers: storing one byte through
the same pointer that previously received a two-byte word does **not** make the
word store dead. Conversely, a wider later store may kill a narrower earlier
subrange when its constant root+offset range proves complete coverage.

Any intervening **call** invalidates every pending store, since a callee could
read the memory. Unknown offsets and target-dependent widths remain conservative.
