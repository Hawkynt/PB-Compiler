# O0197 — Two resident accumulators

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR middle end + x86 back end (register allocation) |
| **Source** | `Ir/Passes/Mem2Reg.cs` (the variable becomes SSA values); `Backend/LinearScanAllocator.cs` — `TryCoalesced` (under SPEED, SI/DI are offered first to what `LivenessAnalysis.LoopCarried` reports); `Backend/CopyCoalescer.cs` |
| **Gate** | `--optimize`; the SI/DI preference needs `$OPTIMIZE SPEED` |
| **Split from** | [O0005](O0005-register-residency.md) |

## What it is

Two hot INTEGER accumulators can be resident at once. Under `$OPTIMIZE SPEED`
SI and DI are offered first to loop-carried values, and any further loop-carried
value (a counter, the loop variable itself) takes another free register from the
ordinary pool. This applies to every loop shape, not only `DO` loops.

## Sample

```basic
$OPTIMIZE SPEED
DIM n%, sum%, cnt%
DO WHILE n% > 0
  sum% = sum% + n%
  cnt% = cnt% + 1
  n% = n% - 1
LOOP
```

## With the optimizer

```asm
    mov     si, [sum]
    mov     di, [cnt]
Top:
    cmp     word ptr [n], 0000h
    jle     Done
    add     si, [n]
    inc     di
    dec     word ptr [n]
    jmp     Top
Done:
    mov     [sum], si
    mov     [cnt], di
```

The listing shows the shape the retired direct emitter produced. The allocator
now keeps `n%` in a register as well, and picks the registers per function.

## Why it is safe

The same argument as [O0196](O0196-do-loop-residency.md), applied to each value
independently.

## Limits

SI and DI are only two registers, so a third preferred value falls back to the
ordinary pool order, and the whole register file is six registers; several hot
dword values at once is the 386 tier ([O0058](O0058-386-register-allocation.md)).
