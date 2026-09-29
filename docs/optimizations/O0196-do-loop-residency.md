# O0196 — DO/WHILE loop accumulator residency

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR middle end + x86 back end (register allocation) |
| **Source** | `Ir/Passes/Mem2Reg.cs` (the variable becomes SSA values); `Backend/LinearScanAllocator.cs` — `TryCoalesced` (under SPEED, SI/DI are offered first to what `LivenessAnalysis.LoopCarried` reports); `Backend/CopyCoalescer.cs` |
| **Gate** | `--optimize`; the SI/DI preference needs `$OPTIMIZE SPEED` |
| **Verified by** | `tests/diff/DIFF96.BAS` |
| **Split from** | [O0005](O0005-register-residency.md) |

## What it is

A hot INTEGER accumulator in a `DO`/`WHILE`/`LOOP` lives in a register across
the loop. The allocator treats every loop shape alike: the accumulator is a
loop-carried SSA value, and under `$OPTIMIZE SPEED` SI and DI are offered to it
first.

## Sample

```basic
$OPTIMIZE SPEED
DIM n%, s%
DO WHILE n% > 0
  s% = s% + n%
  n% = n% - 1
LOOP
```

## With the optimizer

```asm
    mov     si, [s]
Top:
    cmp     word ptr [n], 0000h
    jle     Done
    add     si, [n]
    dec     word ptr [n]
    jmp     Top
Done:
    mov     [s], si
```

The listing shows the shape the retired direct emitter produced. The allocator
now keeps `n%` in a register as well, and picks the registers per function.

## Why it is safe

The loop test is part of the value's live range like the body, so anything in
it that clobbers a register (a call, for instance) is seen by the allocator, which
then keeps the value elsewhere. Every exit, including `EXIT DO`, carries the
current SSA value to its successor, so a read after the loop sees the final
value.

## See also

Because a `DO` loop has no counter, both SI and DI are available —
[O0197](O0197-dual-accumulators.md).
