# O0197 — Two resident accumulators

| | |
|---|---|
| **Status** | ✅ Implemented (DO/WHILE loops) |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Optimize.cs` — `TryEmitDoLoopInRegister` |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` |
| **Split from** | [O0005](O0005-register-residency.md) |

## What it is

A `DO` loop has no counter, so **both** SI and DI are free: two hot INTEGER
accumulators can be resident at once. That is the maximum the 8086 register file
allows — SI and DI are its only callee-stable general registers.

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

## Why it is safe

The same clean-region proof as [O0196](O0196-do-loop-residency.md), applied to
both registers, with both flushed on every exit path.

## Limits

A third simultaneous resident is impossible on this target; several hot values at
once is the 386 tier ([O0058](O0058-386-register-allocation.md)).
