# O0041 — Branch layout and loop alignment

| | |
|---|---|
| **Status** | ✅ Implemented (layout by construction; loop-top alignment under `$CPU 80486`/`80586`) |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.cs` — `AlignLoopTop` and the `IF`/loop emitters |
| **Gate** | layout: always; alignment: `$CPU 80486`+ and `$OPTIMIZE SPEED` |
| **Related** | [O0031](O0031-branch-fusion.md), [O0035](O0035-jump-relaxation.md), [C0002](C0002-486-codegen.md) |
| **Split into** | [O0231](O0231-loop-top-alignment.md), [O0232](O0232-procedure-entry-alignment.md) |

## What it is

**This page covers the branch shape.** The 8086-through-Pentium static predictor
is *forward-not-taken / backward-taken*, and the codegen lays branches out for
it by construction: an `IF` emits `EmitCondition; Jz else` — a **forward**
conditional whose fall-through is the `THEN` body, so the predicted path is the
likely one — and every loop closes with a **backward** `JMP top` / `Jcc top`
back-edge, which is predicted taken, the iterating case.

So the common path is the fall-through and loop continuation by default, with no
pass and no cost.

Loop-top and procedure-entry alignment are the separate entries
[O0231](O0231-loop-top-alignment.md) and
[O0232](O0232-procedure-entry-alignment.md).

## Sample

```basic
$CPU 80486
$OPTIMIZE SPEED
DIM i%, s%
FOR i% = 1 TO 1000 : s% = s% + i% : NEXT
```

## Without the optimizer

```asm
    mov     si, 0001h
Top:                         ; wherever it happens to land
    cmp     si, 03E8h
    jg      Done
    add     di, si
    inc     si
    jmp     Top
Done:
```

## With the optimizer

```asm
    mov     si, 0001h
    nop                      ; pad to the next 16-byte boundary, executed once
    nop
Top:                         ; 16-byte aligned: better fetch and branch-target prefetch
    cmp     si, 03E8h
    jg      Done
    add     di, si
    inc     si
    jmp     Top
Done:
```

## Equivalent BASIC

Unchanged.

## Why it is safe

The alignment pad sits on the entry path, before the loop top label, so the
back-edge never executes it, and `NOP` has no architectural effect anyway. The
branch-sense choice is a layout decision made when the code is generated, not a
rewrite — the differential oracle stays byte-identical because the genuine
compiler makes the same shape choice.
