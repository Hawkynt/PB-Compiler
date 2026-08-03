# O0231 — Hot loop-top alignment

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.cs` — `AlignLoopTop` |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` + `$CPU 80486`/`80586` |
| **Split from** | [O0041](O0041-branch-layout.md) (which is now the branch shape) |

## What it is

Loop tops are NOP-padded to a 16-byte boundary on **every** loop emitter — the
general `FOR`/`DO`, the int16 fast `FOR`, and every register-resident,
pointer-stepped or auto-vectorized loop — so the body starts on a fresh fetch
line.

The pad sits on the fall-through entry path and is skipped by the back edge, so
it executes at most once and is **output-invariant**.

## Sample

```basic
$CPU 80486
$OPTIMIZE SPEED
DIM i%, s%
FOR i% = 1 TO 1000 : s% = s% + i% : NEXT
```

## With the optimizer

```asm
    mov     si, 0001h
    nop                      ; pad to the boundary, executed once
    nop
Top:                         ; 16-byte aligned
    cmp     si, 03E8h
    ...
    jmp     Top              ; the back edge skips the pad
```

## Why it is safe

`NOP` has no architectural effect and the pad is outside the loop body, so the
differential oracle stays byte-identical in *behavior* while the image differs.
The gate matters: on an 8086 there is no fetch line to align to, and the padding
would be pure size.

## Limits

DWORD-aligning hot **data** slots is the remaining piece of the alignment work
([C0002](C0002-486-codegen.md)); selecting *which* loops are hot enough to be
worth padding needs profile data ([O0272](O0272-profile-guided-loop-optimization.md)).
