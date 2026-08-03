# O0349 — x87 value retention across expressions

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [C0003](C0003-x87-scheduling.md), [O0348](O0348-x87-stack-scheduling.md), [O0005](O0005-register-residency.md) |

## The idea

Every float expression today ends with a store and the next one begins with a
load. Keeping a live value **on the FPU stack** between statements — the float
analogue of register residency ([O0005](O0005-register-residency.md)) — removes
both, and keeping a loop-invariant constant resident across an unrolled body
removes a load per iteration.

## Applies to

```basic
DIM x!, y!, z!
y! = x! * 2!                 ' stored...
z! = y! + 1!                 ' ...and immediately reloaded
```

## What it needs

- The eight-deep stack is the constraint: a resident value occupies a slot for
  its whole lifetime, and an expression needing more depth than remains must
  spill — so the allocator is a **depth** allocator, not a register allocator.
- The same flush obligations as integer residency: on every exit path, before
  any call, and before an `ON ERROR` handler can observe memory.
- `EMMS` interaction if MMX code is present ([R0004](R0004-asm-intrinsics.md)) —
  the MMX and x87 register files are the same storage.
