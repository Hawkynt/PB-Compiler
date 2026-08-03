# O0052 — IR: CFG simplification

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR mid-end, last pass of the standard pipeline |
| **Source** | `Ir/Passes/SimplifyCfg.cs` |
| **Related** | [O0044](O0044-ir-sccp.md), [O0051](O0051-ir-if-conversion.md) |

## What it is

Cleanup of the many trivial blocks the lowering emits (`if.next`, `do.latch`,
`for.inc`, …). Two safe, high-value transforms, run to an internal fixpoint:

1. **Trivial-phi elimination** — a phi whose inputs are all the same value *is*
   that value.
2. **Single-predecessor merge** — a block ending in an unconditional branch to a
   successor that has only this predecessor is spliced into it, deleting the
   edge and the block.

## Sample

```basic
DIM i%, s%
FOR i% = 1 TO 3
  s% = s% + i%
NEXT
```

## Before

```llvm
for.body:
  %s.1 = add i16 %s.0, %i
  br label %for.inc
for.inc:
  %i.next = add i16 %i, 1
  br label %for.cond
```

## After

```llvm
for.body:
  %s.1 = add i16 %s.0, %i
  %i.next = add i16 %i, 1
  br label %for.cond
```

## Equivalent BASIC

Unchanged — this is IR housekeeping, not a source-level transform. Its value is
that every later pass sees larger basic blocks, which makes the intra-block
analyses ([O0047](O0047-ir-redundant-memory.md),
[O0048](O0048-ir-dead-store-elimination.md)) reach further.

## Why it is safe

A phi with identical inputs has the same value on every incoming edge, so
replacing it is substitution of equals. A block with a single predecessor whose
terminator is an unconditional branch to it can only ever be entered from that
predecessor, so concatenating the two preserves every path exactly. Phi inputs
in successors are updated to name the merged block.
