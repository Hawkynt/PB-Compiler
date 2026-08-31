# O0052 — IR: CFG simplification

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR mid-end, last pass of the standard pipeline |
| **Source** | `Ir/Passes/SimplifyCfg.cs` |
| **Related** | [O0044](O0044-ir-sccp.md), [O0051](O0051-ir-if-conversion.md), [O0107](O0107-branch-folding-through-phi.md) |

## What it is

Cleanup of the many trivial blocks the lowering and earlier IR passes emit
(`if.next`, `do.latch`, `for.inc`, …). The core canonicalizations run to an
internal fixpoint:

1. **Trivial-phi elimination** — a phi whose inputs are all the same value *is*
   that value; self-references do not prevent the fold.
2. **Forwarding-block elimination** — a block containing only phis plus an
   unconditional branch disappears when its incoming edges can be redirected
   safely. Successor phis are translated edge-by-edge, so a bridge phi such as
   `phi [10,left], [20,right]` becomes two direct successor inputs instead of
   keeping an otherwise empty join alive.
3. **Single-predecessor merge** — a block ending in an unconditional branch to a
   successor that has only this predecessor is spliced into it, deleting the
   edge and the block.

The same pass also hosts narrowly scoped CFG peepholes that build on those
primitives, such as [O0107](O0107-branch-folding-through-phi.md).

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

A PHI-only forwarding join is handled too:

```llvm
left:                       right:
  br label %bridge            br label %bridge

bridge:
  %v = phi i16 [10, %left], [20, %right]
  br label %exit

exit:
  %r = phi i16 [%v, %bridge]
```

becomes:

```llvm
left:                       right:
  br label %exit              br label %exit

exit:
  %r = phi i16 [10, %left], [20, %right]
```

## Equivalent BASIC

Unchanged — this is IR housekeeping, not a source-level transform. Its value is
that every later pass sees larger basic blocks and fewer artificial joins, which
makes local analyses ([O0047](O0047-ir-redundant-memory.md),
[O0048](O0048-ir-dead-store-elimination.md)) reach further and gives the
SSA/CFG passes less scaffolding to reason about.

## Why it is safe

A phi with identical inputs has the same value on every incoming edge, so
replacing it is substitution of equals. A forwarding block executes no ordinary
instruction; redirecting its predecessors therefore skips no side effect. When
the value observed by a successor phi is one of the forwarding block's phis, the
incoming value for each predecessor is exactly the value that would have flowed
through that edge, so expanding the successor phi preserves SSA semantics.

The forwarding rewrite is intentionally conservative. It does **not** run when a
predecessor already has a direct edge to the successor (which could require
edge-distinct phi values), when the bridge phi has a non-phi use that would have
to move or be cloned, for switch/indirect predecessor edges without a dedicated
retargeting API, for address-taken blocks, or for the loop-entry/back-edge shapes
left to loop-aware passes.
