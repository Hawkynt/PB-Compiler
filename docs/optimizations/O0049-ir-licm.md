# O0049 — IR: loop-invariant code motion

| | |
|---|---|
| **Status** | ✅ Implemented (non-trapping instructions, innermost loops first) |
| **Stage** | IR mid-end |
| **Source** | `Ir/Passes/Licm.cs` |
| **Related** | [O0028](O0028-loop-invariant-code-motion.md) (the AST tier), [O0060](O0060-memory-ssa.md) |

## What it is

Natural loops are found from CFG back edges via the dominator tree. Within each,
an instruction that is **pure, speculatable and loop-invariant** — every operand
defined outside the loop, transitively — is hoisted into the loop's preheader,
so it runs once instead of every iteration.

Loops are processed innermost-first, so over repeated runs a value can climb out
of several nested loops.

## Sample

```basic
DIM i%, w%, h%, a%(0 TO 99)
FOR i% = 0 TO 99
  a%(i%) = w% * h%
NEXT
```

## Before

```llvm
loop:
  %i = phi i16 [ 0, %entry ], [ %i.next, %latch ]
  %0 = mul i16 %w, %h        ; invariant, recomputed every iteration
  ...
```

## After

```llvm
preheader:
  %0 = mul i16 %w, %h
  br label %loop
loop:
  %i = phi i16 [ 0, %preheader ], [ %i.next, %latch ]
  ...
```

## Equivalent BASIC

```basic
DIM t%
t% = w% * h%
FOR i% = 0 TO 99 : a%(i%) = t% : NEXT
```

## Why it is safe

The preheader executes even when the loop body does not, so only **non-trapping**
instructions are hoisted: integer and float division are left in place (they can
fault), and so are loads. That makes speculative execution in the preheader
incapable of introducing a fault the original program would not have hit — the
same zero-trip-safety argument as the AST-tier pass
([O0028](O0028-loop-invariant-code-motion.md)).

[O0060 Memory SSA](O0060-memory-ssa.md) now supplies the missing memory-mutation
proof for loads: it can tell whether any loop-carried definition may clobber a
particular read. That is necessary but not sufficient for hoisting. A load also
needs proof that moving it to the preheader cannot newly execute or fault on a
zero-trip path.

## Limits

Load hoisting remains deliberately disabled until LICM has a non-trapping or
must-execute proof for the candidate load. Memory SSA is available for the alias
side of that decision; the remaining blocker is speculation safety, not memory
dependence. Sinking rarely-executed computations into their branch is roadmap.
