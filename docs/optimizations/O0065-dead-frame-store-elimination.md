# O0065 — Dead frame-store elimination

| | |
|---|---|
| **Status** | ⬜ Planned (blocked on instruction recording) |
| **Stage** | Assembler |
| **Related** | [O0034](O0034-redundant-load-elimination.md), [O0038](O0038-instruction-scheduling.md), [O0060](O0060-memory-ssa.md) |

## The idea

Once [redundant-load elimination](O0034-redundant-load-elimination.md) has
removed the last reader of a spill or CSE cell, the store *into* it is dead. The
max-scan idiom leaves exactly one such `MOV [BP-8],AX` per iteration — the last
instruction in that loop with nothing left to justify it.

## Applies to

```basic
$OPTIMIZE SPEED
DIM a%(0 TO 99), i%, m%
FOR i% = 0 TO 99
  IF a%(i%) > m% THEN m% = a%(i%)
NEXT
```

## Today

```asm
    mov     ax, [bx]
    mov     [bp-8], ax       ; CSE define — nothing reads it any more
    cmp     ax, di
    jle     Skip
    mov     di, ax
Skip:
```

## Planned

```asm
    mov     ax, [bx]
    cmp     ax, di
    jle     Skip
    mov     di, ax
Skip:
```

## Why it is blocked

Proving the store dead means proving that **nothing else touches the cell**, and
the def/use records the assembler keeps do not cover every instruction that can
reach memory: `LEA`, `PUSH mem`, the `ADD [mem],imm` read-modify-write forms and
the indirect jumps all touch memory unrecorded. A whole-procedure "no reader"
scan over the records alone is therefore unsound.

Note that this does **not** affect load forwarding itself, which requires an
unbroken chain of *recorded*, byte-adjacent instructions between store and load —
an unrecorded instruction is precisely what ends its scan.

## What it needs

Either recording those forms too, or having the code generator declare the
compiler-temp region of the frame (so the assembler knows which cells are
private to it). Either unblocks the pass.
