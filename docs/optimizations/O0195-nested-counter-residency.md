# O0195 — Nested FOR counter residency

| | |
|---|---|
| **Status** | ✅ Implemented (two levels — the hard limit) |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Optimize.cs` — `TryEmitNestedForCounterInRegister`, `IsNestedRegisterableFor` |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` |
| **Verified by** | `tests/diff/DIFF90.BAS` |
| **Split from** | [O0005](O0005-register-residency.md) |

## What it is

An inner INTEGER `FOR` under an SI-resident outer loop keeps **its** counter in
DI, instead of giving DI to an accumulator. Both counters then live in the two
safe index registers and neither spills to the stack.

Two levels is the hard limit: SI and DI are the only callee-stable index
registers on an 8086 and they have no 8-bit halves, so a third level falls back
to memory (`IsNestedRegisterableFor` keeps the nest leaf-only).

## Sample

```basic
$OPTIMIZE SPEED
DIM i%, j%, t%
FOR i% = 1 TO 100
  FOR j% = 1 TO 100
    t% = t% + 1
  NEXT
NEXT
```

## With the optimizer

```asm
    mov     si, 0001h        ; outer counter
OuterTop:
    mov     di, 0001h        ; inner counter
InnerTop:
    ...
    inc     di
    cmp     di, 0064h
    jle     InnerTop
    mov     [j], di          ; flushed: a post-loop read sees the end value
    inc     si
```

## Why it is safe

The inner counter is flushed to its cell on loop exit, so a post-loop read sees
the increment-then-test end value (QUIRK 2.28). Both loops must be clean for
their register.
