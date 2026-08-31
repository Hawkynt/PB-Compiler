# O0351 — Pointer and handle check elimination

| | |
|---|---|
| **Status** | ✅ Implemented (explicit dominated null tests) |
| **Stage** | Mid-end |
| **IR** | `PowerBasic.Compiler/Ir/Passes/PointerCheckElim.cs` |
| **Related** | [O0097](O0097-repeated-comparison-elimination.md), [O0045](O0045-ir-correlated-value-propagation.md), [O0181](O0181-empty-string-comparison.md) |

## The idea

PB has no null-pointer *fault*, but it has the same shape of redundant test:
a pointer or string handle checked for zero, dereferenced, then checked again —
and array-descriptor validity tests before every access to a dynamic array.

A check **dominated by an explicit successful null test** is redundant. A dereference alone is not evidence.

## Applies to

```basic
DIM p AS INTEGER POINTER
IF p <> 0 THEN
  PRINT @p
  IF p <> 0 THEN PRINT "still valid"     ' provably true
END IF
```

## IR implementation

`PointerCheckElim` walks the dominator tree and records only explicit `ptr == null` / `ptr != null`
branch facts. A later comparison of the same SSA value is folded when the corresponding true or false
edge dominates it. Facts are tied to the SSA value rather than to a storage location, so reloading a
pointer or string-handle cell after a call or store produces a new value and cannot accidentally reuse
stale knowledge.

The pass deliberately ignores loads/dereferences. That negative rule is tested: a null near pointer may
read segment zero on PB's DOS target, so surviving a dereference says nothing about nullness.

## What it needs

- The dominator-scoped fact propagation of
  [O0097](O0097-repeated-comparison-elimination.md) — the same machinery, a
  different predicate.
- A rule about what a **dereference implies**: on a machine with no memory
  protection, dereferencing a null pointer does not fault, it reads segment
  zero — so "it was dereferenced, therefore it was non-null" is *not* sound
  here. Only an explicit preceding test counts.
- String handles are opaque pointers in the IR, so the same explicit-null rule applies without
  teaching the pass the string-manager representation.
