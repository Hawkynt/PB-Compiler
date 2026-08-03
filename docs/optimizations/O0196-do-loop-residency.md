# O0196 — DO/WHILE loop accumulator residency

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Optimize.cs` — `TryEmitDoLoopInRegister` |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` |
| **Verified by** | `tests/diff/DIFF96.BAS` |
| **Split from** | [O0005](O0005-register-residency.md) |

## What it is

The first generalization past the `FOR`-loop shape: a `DO`/`WHILE`/`LOOP` has no
counter, so **SI is free**. When the body and the loop tests are SI/DI-clean,
one hot INTEGER accumulator becomes SI-resident.

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

## Why it is safe

The loop **tests** must be SI-clean as well as the body — a `DO WHILE` evaluates
its condition every iteration, and a condition that clobbered SI would destroy
the resident value. The accumulator is flushed on every exit, including `EXIT
DO`.

## See also

Because a `DO` loop has no counter, both SI and DI are available —
[O0197](O0197-dual-accumulators.md).
