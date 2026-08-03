# O0047 — IR: load/store forwarding

| | |
|---|---|
| **Status** | ✅ Implemented (intra-block) |
| **Stage** | IR mid-end, after GVN |
| **Source** | `Ir/Passes/RedundantMemory.cs` |
| **Related** | [O0042](O0042-ir-mem2reg.md), [O0046](O0046-ir-gvn.md), [O0048](O0048-ir-dead-store-elimination.md) |

## What it is

The memory analogue of what mem2reg does for promotable scalars, for the
addresses that stay in memory — array elements, BYREF targets. Within a block
it:

- **forwards** a load from the value most recently stored to the same address;
- **reuses** an earlier load of an address nothing has written since.

It runs after [GVN](O0046-ir-gvn.md) so that congruent address computations are
already a single SSA value, which makes address comparison a reference check
plus a small sound alias test.

## Sample

```basic
DIM a%(0 TO 9), t%
a%(3) = 42
t% = a%(3) + a%(3)
```

## Before

```llvm
  %p = getelementptr [10 x i16], ptr @a, i16 0, i16 3
  store i16 42, ptr %p
  %0 = load i16, ptr %p
  %1 = load i16, ptr %p
  %2 = add i16 %0, %1
```

## After

```llvm
  %p = getelementptr [10 x i16], ptr @a, i16 0, i16 3
  store i16 42, ptr %p
  %2 = add i16 42, 42        ; then folded to 84 by instcombine
```

## Equivalent BASIC

```basic
a%(3) = 42
t% = 84
```

## Why it is safe

The alias test is sound and deliberately small: distinct allocas never alias, and
the same base at distinct constant offsets never aliases. Any **may-aliasing**
store, and any **call** (which could read or write memory), conservatively
invalidates the affected cache entries — so a forwarded value is only ever one
that nothing in between could have changed.
