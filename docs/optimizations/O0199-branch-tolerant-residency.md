# O0199 — Residency across a conditional

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR middle end + x86 back end (register allocation) |
| **Source** | `Ir/Passes/Mem2Reg.cs` (the variable becomes SSA values); `Backend/LinearScanAllocator.cs` — `TryCoalesced` (under SPEED, SI/DI are offered first to what `LivenessAnalysis.LoopCarried` reports); `Backend/CopyCoalescer.cs` |
| **Gate** | `--optimize`; the SI/DI preference needs `$OPTIMIZE SPEED` |
| **Verified by** | `tests/diff/DIFF91.BAS` |
| **Split from** | [O0005](O0005-register-residency.md) |

## What it is

A **conditional** (`IF`/`ELSEIF`/`ELSE`) in the loop body does not stop the
counter or the accumulator from staying in registers. The value that an arm may
update meets its other path in a phi at the merge, and the allocator keeps the
whole live range in one register; a branch touches no general register, so the
residents survive it.

## Sample

```basic
$OPTIMIZE SPEED
DIM i%, s%, a%(0 TO 99)
FOR i% = 0 TO 99
  IF a%(i%) > 0 THEN s% = s% + a%(i%)
NEXT
```

## With the optimizer

```asm
    mov     si, 0000h        ; counter
    xor     di, di           ; accumulator survives the branch
Top:
    ...
    cmp     ax, 0000h
    jle     Skip
    add     di, ax
Skip:
    inc     si
    jmp     Top
```

## Why it is safe

An arm that clobbers registers (a call, a string operation, inline asm) declares
those clobbers, and the allocator keeps a value live across it only in a register
it leaves alone, or spills it.
