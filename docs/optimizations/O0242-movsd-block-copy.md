# O0242 — DWORD block copy for TYPE and `LSET`

| | |
|---|---|
| **Status** | 🟡 Partial — whole-`TYPE` copies run DWORD-wide on an optimized 386+ target; the 8086 word-wide form is not implemented |
| **Stage** | IR middle end + runtime |
| **Source** | `Ir/IrLowering.cs` (record copies as `llvm.memcpy`); `Ir/Passes/MemoryRoutineSpecialization.cs` (small constant sizes inline); `Runtime/DosRuntime.Memory.cs` — `rt_memcpy`; `Runtime/DosRuntime.Core.cs` — `EmitRepMovsbWidened` |
| **Gate** | `--optimize` (word-wide, 8086-safe) + `$CPU 80386` (dword-wide) |
| **Verified by** | `tests/diff/DIFF23.BAS` |
| **Split from** | [R0003](R0003-string-engine.md) / [O0015](O0015-udt-zero-cost.md) |

## What it is

Whole-`TYPE` copies, `LSET` and BCD block moves run **word-wide** (`REP MOVSW`,
8086-safe) under the optimizer and **DWORD-wide** (`REP MOVSD`) under
`$CPU 80386`, with odd tails byte-copied.

On the IR path a whole-record assignment lowers to `llvm.memcpy`.
`MemoryRoutineSpecialization` expands a small constant-size copy into scalar
loads and stores at the target's width; anything larger becomes a call to
`rt_memcpy`, whose `EmitRepMovsbWidened` body runs `REP MOVSD` plus a `REP MOVSB`
tail when the optimizer is on and the target has 32-bit registers, and plain
`REP MOVSB` otherwise. The 8086 `REP MOVSW` form described here is not
implemented.

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
