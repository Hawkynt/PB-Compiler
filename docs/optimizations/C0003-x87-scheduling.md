# C0003 — x87 scheduling

| | |
|---|---|
| **Status** | ✅ Implemented as a scheduling pseudo-resource; FPU-stack residency across unrolled bodies is ⬜ planned |
| **Stage** | Assembler |
| **Source** | `Asm/Assembler.Fpu.cs` (`FpuMemory`, `FpuStack`), `Asm/Assembler.Schedule.cs` (`_FPUSTACK`) |
| **Gate** | `pb36` + `$OPTIMIZE SPEED` (the scheduler's gate) |
| **Related** | [O0038](O0038-instruction-scheduling.md), [O0013](O0013-promotion-lowering.md), [O0037](O0037-fixed-point-for-counters.md) |

## What it is

The x87 is a **stack** machine, so its instructions are not freely reorderable
among themselves — but they are largely independent of the integer unit. The
scheduler models this with a pseudo-resource bit beyond the eight register
slots: every FPU instruction reads *and* writes `_FPUSTACK`, which forces
RAW + WAW ordering among FPU instructions while letting independent integer work
interleave around them.

Memory operands of FPU instructions are recorded conservatively as read **and**
written (covering `FLD` reads, `FST`/`FSTP`/`FIST` writes and read-modify
arithmetic alike). Segment-overridden operands stay unrecorded and remain
scheduling barriers.

## Sample

```basic
$OPTIMIZE SPEED
DIM d AS DOUBLE, n%, m%
d = d * 1.5
m% = n% + 1                  ' independent integer work
```

## Before scheduling

```asm
    fld     qword ptr [d]
    fmul    qword ptr [c15]
    fstp    qword ptr [d]
    mov     ax, [n]
    inc     ax
    mov     [m], ax
```

## After scheduling

```asm
    fld     qword ptr [d]
    mov     ax, [n]          ; integer work overlaps the FPU latency
    fmul    qword ptr [c15]
    inc     ax
    fstp    qword ptr [d]
    mov     [m], ax
```

The three FPU instructions keep their exact relative order.

## Equivalent BASIC

Unchanged.

## Why it is safe

The pseudo-resource makes every FPU instruction depend on every other one, so no
stack-order-sensitive pair can ever be swapped — the scheduler can only move
*integer* instructions across them, and only when the register, flag and memory
dependency model says they are independent.

## What is still planned

The deeper form: keeping loop-invariant constants **resident on the FPU stack**
across unrolled bodies (instead of reloading them), and avoiding `FSTSW`/`SAHF`
stalls where a comparison can run on pre-truncated integer values — which is
what [O0037](O0037-fixed-point-for-counters.md) already does for the one shape
where the values are provably exact.
