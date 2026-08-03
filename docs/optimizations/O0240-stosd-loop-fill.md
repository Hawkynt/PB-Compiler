# O0240 — `REP STOSD` constant loop fill

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Optimize.cs` — `TryEmitForIdiom` + the 386 widening |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` + `$CPU 80386` |
| **Verified by** | `tests/diff/DIFF75.BAS` |
| **Split from** | [C0001](C0001-386-codegen.md) |

## What it is

The constant `FOR`-loop array fill that [O0227](O0227-constant-fill-stosw.md)
lowers to `REP STOSW` widens further to `REP STOSD` under `$CPU 80386`: the
16-bit value is **broadcast into both halves of EAX**, so one store covers two
elements.

## Sample

```basic
$CPU 80386
$OPTIMIZE SPEED
DIM a%(0 TO 999), i%
FOR i% = 0 TO 999
  a%(i%) = 7
NEXT
```

## With the optimizer

```asm
    mov     eax, 00070007h   ; the 16-bit value in both halves
    lea     di, [a]
    mov     ecx, 000001F4h   ; 500 dwords for 1 000 words
    rep     stosd
    mov     word ptr [i], 03E8h
```

## Why it is safe

The broadcast makes the dword store byte-identical to two word stores of the
same value, and an odd element count writes its tail at word width. The counter
end value is stored explicitly, as for every idiom replacement.
