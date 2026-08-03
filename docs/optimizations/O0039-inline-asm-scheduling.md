# O0039 — Inline-assembly instruction scheduling

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Between the inline-asm parser and the text assembler |
| **Source** | `CodeGen/InlineAsmScheduler.cs` |
| **Gate** | `pb36` + `$OPTIMIZE SPEED`, no error handler in scope |
| **Verified by** | 241-battery oracle (byte-identical), scheduler unit tests, execution tests |
| **Related** | [O0038](O0038-instruction-scheduling.md), [R0004](R0004-asm-intrinsics.md) |

## What it is

A run of three or more consecutive single-instruction `!` lines is reordered to
group memory and ALU operations and let independent dependency chains
interleave — better load/store-port and U-V-pipe utilization on a 486/Pentium.

A conservative per-line def/use model (register RAW/WAR/WAW, flags, and memory
where any write-involving pair with possibly-aliasing operands is ordered)
yields a dependency partial order; the emitted result is one valid topological
order of it.

## Sample

```basic
$OPTIMIZE SPEED
DIM a AS INTEGER, b AS INTEGER
! MOV AX, a
! ADD AX, 5
! MOV BX, b
! ADD BX, 7
```

## As written

```asm
    mov     ax, a
    add     ax, 5
    mov     bx, b
    add     bx, 7
```

## As scheduled

```asm
    mov     ax, a
    mov     bx, b
    add     ax, 5
    add     bx, 7
```

## Equivalent BASIC

Unchanged — the assembly block computes the same values in the same registers.

## Why it is safe

Safety is by construction: the scheduler only **decides the order**; emission
stays the unchanged text assembler. Any line it cannot model with certainty — an
unknown mnemonic, a jump, a label, a segment/FPU/SIMD operand, a
multi-instruction line — makes the whole block non-reorderable, and it is emitted
verbatim. Reordering must also not be observable through a fault's resume point,
hence the "no error handler in scope" gate.

A variable named in an inline-asm body is treated as **live** by the optimizer
(its data slot and stores survive), so hand-written asm keeps working with the
optimizer on.
