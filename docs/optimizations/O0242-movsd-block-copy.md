# O0242 — DWORD block copy for TYPE and `LSET`

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Optimize.cs` — `#region C1/R3 block-move widening` |
| **Gate** | `--optimize` (word-wide, 8086-safe) + `$CPU 80386` (dword-wide) |
| **Verified by** | `tests/diff/DIFF23.BAS` |
| **Split from** | [R0003](R0003-string-engine.md) / [O0015](O0015-udt-zero-cost.md) |

## What it is

Whole-`TYPE` copies, `LSET` and BCD block moves run **word-wide** (`REP MOVSW`,
8086-safe) under the optimizer and **DWORD-wide** (`REP MOVSD`) under
`$CPU 80386`, with odd tails byte-copied.

## Sample

```basic
$CPU 80386
TYPE Buffer
  data AS STRING * 256
END TYPE
DIM a AS Buffer, b AS Buffer
b = a
```

## Without / with

```asm
    mov     cx, 0100h        ; without: 256 byte moves
    rep     movsb

    mov     ecx, 00000040h   ; with: 64 dword moves
    rep     movsd
```

## Why it is safe

Same bytes, wider unit, explicit tail. The source and destination of a whole-UDT
assignment are distinct storage (a self-copy is elided outright —
[O0215](O0215-udt-self-copy-elision.md)), so `REP MOVSD`'s forward order is
never observable.
