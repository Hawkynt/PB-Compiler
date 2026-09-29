# O0019 — Definite-assignment zero elision

| | |
|---|---|
| **Status** | ✅ Implemented for stack frames; array/heap zero-fill elision is [O0068](O0068-array-zero-fill-elision.md) |
| **Stage** | IR analysis + x86 back end (frame prologue) |
| **Source** | `Ir/Analysis/IrSlotInitialization.cs` — `NeedingZeroStart`; `Backend/InstructionSelector.cs` — `SelectAlloca` (records `ZeroStartSlots`); `Backend/MachineEmitter.cs` — `ZeroStartRange`, `EmitFunction` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF22.BAS` (locals must read `0`/`""` before assignment on every call, with and without elision) |
| **Related** | [O0002](O0002-dead-code-elimination.md), [O0070](O0070-leaf-frame-elision.md) |

## What it is

PowerBASIC guarantees that `LOCAL`s start at 0 / `""` on **every** invocation,
so the prologue zero-fills the whole frame with a `REP STOSW`. A slot that is
always written before anything reads it cannot show that zero, so filling it is
pure cost.

`IrSlotInitialization.NeedingZeroStart` is a forward must-analysis over the
function's control-flow graph: a slot is safe when, on every path from the
entry, a store of its whole type reaches it before any load of it or any other
use of its address (a GEP into it, a call it is passed to, a store of the
address), which counts as a read. Optimized selection records the slots that
are *not* safe, and the prologue fills only the smallest contiguous run of the
frame covering them — usually nothing, since spill and scratch slots are always
written first. Unoptimized, the whole frame is filled.

## Sample

```basic
SUB Work
  LOCAL a%, b%, c%
  a% = 1
  b% = 2
  c% = a% + b%
  PRINT c%
END SUB
```

## Without the optimizer

```asm
Work:
    push    bp
    mov     bp, sp
    sub     sp, 0006h
    push    ss
    pop     es
    lea     di, [bp-6]
    mov     cx, 0003h
    xor     ax, ax
    rep     stosw            ; every invocation, whether needed or not
    ...
```

## With the optimizer

```asm
Work:
    push    bp
    mov     bp, sp
    sub     sp, 0006h
    ...                      ; the fill is gone; every local is written first
```

## Equivalent BASIC

```basic
SUB Work
  LOCAL a%, b%, c%          ' the zero-initialization guarantee is unobservable here
  a% = 1 : b% = 2 : c% = a% + b%
  PRINT c%
END SUB
```

## Why it is safe

- The analysis is a must-analysis over every path: a slot one path reads
  before writing keeps its zero, and so does a slot written only partially.
- An `ON ERROR` handler or inline assembly adds control or memory edges the
  graph does not show, so every slot of such a function keeps its zero.
- Assigning a dynamic string loads the old handle to free it, which is a read,
  so a handle slot that could be freed before it was written keeps its zero.

## Limits

Skipping an array's allocation zero-fill needs a loop-fill dominance proof over
the value lattice — [O0068](O0068-array-zero-fill-elision.md). Dropping the BP
frame entirely for leaf procedures is [O0070](O0070-leaf-frame-elision.md).
