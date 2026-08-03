# O0046 — IR: global value numbering

| | |
|---|---|
| **Status** | ✅ Implemented |
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

## Why it is safe

Only **pure** instructions are numbered — nothing that traps, reads memory or
has an effect. Replacement is restricted to instructions the leader dominates,
so every use is reached by a definition, and the IR verifier checks that
invariant after the pass when `VerifyEachPass` is on.

## Limits

Load GVN through memory needs memory SSA ([O0060](O0060-memory-ssa.md));
intra-block load/store forwarding is [O0047](O0047-ir-redundant-memory.md).
