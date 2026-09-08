# O0046 — IR: global value numbering

| | |
|---|---|
| **Status** | ✅ Implemented (including MemorySSA-backed load GVN) |
| **Stage** | IR mid-end |
| **Source** | `Ir/Passes/Gvn.cs` |
| **Related** | [O0003](O0003-common-subexpression-elimination.md) (the AST tier), [O0047](O0047-ir-redundant-memory.md), [O0060](O0060-memory-ssa.md) |

## What it is

Two pure instructions that compute the same function of the same operands are
**congruent**, and the one dominated by the other is replaced by it. The value
table is scoped to the **dominator tree**, so a leader is only reused where it
provably dominates the use — which is what keeps the result valid SSA.

Commutative operands are canonically ordered, so `a + b` and `b + a` are
recognized as equal.

Loads participate too, but only through [O0060 Memory SSA](O0060-memory-ssa.md):
the key contains the value type, pointer SSA value and alias-aware clobbering
memory version. An intervening may-alias write therefore makes two otherwise
identical loads different; a proven-disjoint write does not.

This supersedes block-local CSE: it eliminates redundancy *across* blocks, not
just within one.

## Sample

```basic
DIM x%, y%, o%, p%
o% = x% * 320 + y%
IF y% > 0 THEN p% = y% + x% * 320
```

## Before

```llvm
  %0 = mul i16 %x, 320
  %1 = add i16 %0, %y
  ...
then:
  %2 = mul i16 %x, 320        ; congruent with %0
  %3 = add i16 %y, %2         ; congruent with %1 after canonicalization
```

## After

```llvm
  %0 = mul i16 %x, 320
  %1 = add i16 %0, %y
  ...
then:
  ; %2 and %3 replaced by %0 and %1
```

## Equivalent BASIC

```basic
DIM t%
t% = x% * 320 + y%
o% = t%
IF y% > 0 THEN p% = t%
```

## Load example

```llvm
  %a = load i16, ptr %p
  store i16 7, ptr %unrelated
  br label %next
next:
  %b = load i16, ptr %p
```

When alias analysis proves `%unrelated` cannot overlap `%p`, Memory SSA gives
`%a` and `%b` the same clobbering version. `%a` dominates `%b`, so `%b` is replaced
by `%a`. A store that may overlap `%p`, or an opaque call, changes the memory
version and blocks the fold.

## Why it is safe

Pure arithmetic/value instructions have no observable effect beyond their result.
For loads, equality additionally requires the same alias-aware MemorySSA clobber,
so the bytes read cannot have been changed by an intervening known write. The
leader must dominate the replacement site in all cases, which means eliminating
the later load never causes an earlier load to execute on a path where it did not
already execute.

The IR verifier checks the resulting ordinary SSA dominance invariant after the
pass when `VerifyEachPass` is on.

## Limits

MemorySSA-backed GVN removes dominated repeated loads but does not move them.
Hoisting loads out of loops is a separate LICM legality problem because a preheader
load may newly execute on a zero-trip path; see [O0049](O0049-ir-licm.md).
Intra-block store-to-load forwarding remains [O0047](O0047-ir-redundant-memory.md).
