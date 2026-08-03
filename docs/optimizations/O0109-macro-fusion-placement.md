# O0109 — Macro-fusion-aware placement

| | |
|---|---|
| **Status** | ⬜ Planned (no effect before the Core-era targets) |
| **Stage** | Assembler scheduling |
| **Related** | [O0038](O0038-instruction-scheduling.md), [O0081](O0081-flag-reuse.md), [O0174](O0174-target-cost-models.md) |

## The idea

Modern x86 cores fuse a `CMP`/`TEST` with the conditional branch that consumes
it into a single micro-op — but only when the two are **adjacent** and only for
certain operand forms. Two consequences for the compiler:

1. the scheduler must not insert an independent instruction between a compare
   and its branch, even though the dependency model permits it;
2. comparisons should be canonicalized into the operand forms the target's
   fusion rules accept (register/immediate rather than memory/immediate on some
   cores, and the branch condition kinds differ per generation).

This is directly at odds with what an 8086 wants, where filling the slot between
a compare and a branch with independent work is *good* — the execution unit has
something to do while the bus unit refills the prefetch queue. Two targets, two
opposite rules.

## Applies to

Every conditional in the program; nothing changes at the source level.

## Today

[O0038](O0038-instruction-scheduling.md) treats conditional jumps as
flag-reading instructions with no clobber and may reorder independent work
across them.

## What it needs

- [O0174](O0174-target-cost-models.md), with a per-target fusion table plus a
  scheduler constraint ("do not separate this pair") that the current dependency
  model has no way to express — it only knows *must-follow*, not
  *must-be-adjacent*.
