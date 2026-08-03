# O0038 — Instruction scheduling (assembler level)

| | |
|---|---|
| **Status** | ✅ Implemented (output-preserving list scheduler over the final byte stream) |
| **Stage** | Assembler, after every codegen transform |
| **Source** | `Asm/Assembler.Schedule.cs` — `RunSchedule` |
| **Gate** | `pb36` + `$OPTIMIZE SPEED` (mutually exclusive with the peephole) |
| **Verified by** | byte-identical across all 241 differential batteries with the scheduler active |
| **Related** | [O0008](O0008-peephole-zero-idiom.md), [O0039](O0039-inline-asm-scheduling.md), [O0072](O0072-register-reassignment.md), [C0003](C0003-x87-scheduling.md) |

## What it is

A dependency-driven list scheduler runs on the **emitted bytes** — downstream of
unrolling, inlining and constant folding, because it operates on instructions
rather than on the AST.

Each recorded instruction carries a conservative def/use descriptor: word
register reads/writes (a byte half maps to its word slot), flags, and memory
with direct-cell / `[BP+disp]` stack / unknown-indexed aliasing. The scheduler
finds maximal contiguous **fixup-free, label-free windows** — any *unrecorded*
instruction (a jump, a `CALL`, the implicit-AX `MUL`/`DIV`, an FPU/SIMD op) is a
byte gap that ends the window — builds the dependency partial order, and rewrites
the window's instruction byte-blocks in a topological order that issues loads
first (latency hiding) and clusters memory and ALU work.

Recorded instruction set: `MOV`, the ALU group, `INC`/`DEC`, `NEG`/`NOT`, the
shift family, `LEA`, `TEST` and two-operand `IMUL` — so a shift, an increment or
an address computation no longer splits a window; the scheduler reorders
*through* them (flag writers pin a following `ADC`/`SBB`, `LEA` contributes its
address-register reads, CL-count shifts read CL).

## Sample

```basic
$OPTIMIZE SPEED
DIM a%, b%, x%, y%
x% = a% + 5
y% = b% + 7
```

## Before scheduling

Two serialized chains, each load immediately consumed:

```asm
    mov     ax, [a]
    add     ax, 0005h
    mov     [x], ax
    mov     bx, [b]
    add     bx, 0007h
    mov     [y], bx
```

## After scheduling

Both loads issue first, so each add finds its operand ready:

```asm
    mov     ax, [a]
    mov     bx, [b]
    add     ax, 0005h
    add     bx, 0007h
    mov     [x], ax
    mov     [y], bx
```

## Equivalent BASIC

Unchanged — the scheduler only chooses an order among independent operations.

## Why it is safe

Permuting whole instruction blocks inside a window needs **no position fixups**:
the bytes are unchanged in length and nothing inside the window is referenced
from outside it, so the image is byte-for-byte the same instructions in a
different order. Any instruction the model cannot describe with certainty ends
the window. It is gated to programs with no error handler in scope, since a
fault's resume point would otherwise make the order observable.

DOSBox models no pipeline or cache, so the *benefit* is unmeasurable here — only
the correctness is verified.

## Limits

The remaining amplifier is **register reassignment** to break the AX-centric
serialization, which is a distinct subsystem rather than an extension of the
scheduler ([O0072](O0072-register-reassignment.md)): it needs codegen-supplied
liveness and re-encoding, because renaming `AX` → `BX` turns the accumulator
short form into the longer modrm form and is therefore not length-preserving.
