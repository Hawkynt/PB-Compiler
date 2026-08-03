# O0030 — Induction-variable strength reduction

| | |
|---|---|
| **Status** | ✅ Implemented (rank-1 INTEGER and LONG arrays, read and store, plus the accumulate shape) |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Optimize.cs` — `#region O6b`, `MatchSteppedAccumulateBody` |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED`, constant `STEP`, no `$ERROR` checking, no aliasing/calls |
| **Verified by** | `tests/diff/DIFF64.BAS` (read), `DIFF65.BAS` (store), `DIFF76.BAS` (LONG), scenario `AccumulateOverArrayIsHandQuality` |
| **Related** | [O0004](O0004-strength-reduction.md), [O0005](O0005-register-residency.md), [O0036](O0036-constant-subscript-folding.md) |

## What it is

A loop that walks an array by its counter does not need to recompute
`base + (i − lbound) * elementSize` every iteration: the address is itself an
induction variable. The emitter steps a DS-relative pointer by
`elementSize * step` per iteration instead, so the hot path becomes a plain load
or store with no multiply and no address arithmetic.

Covered shapes:

- element **read** `x% = a%(i%)` — 2-byte and 4-byte (LONG) elements;
- element **store** `a%(i%) = expr` (the value lands in AX through the ordinary
  expression emitter, BX saved/restored across it, then `MOV [BX],AX`; `expr`
  must not reference the array, so no read-after-write through the stepped
  pointer is possible);
- the **accumulate** loop `acc = acc OP a(i)` — the commonest loop there is —
  keeps its element pointer in **BX** across the whole loop, alongside the SI
  counter and the DI accumulator ([O0005](O0005-register-residency.md)).

## Sample

```basic
$OPTIMIZE SPEED
DIM a%(0 TO 999), i%, s%
FOR i% = 0 TO 999
  s% = s% + a%(i%)
NEXT
```

## Without the optimizer

Ten instructions per iteration, including a multiply that is also an 80186
instruction:

```asm
Top:
    mov     ax, [i]
    cmp     ax, 03E7h
    jg      Done
    mov     ax, [i]
    imul    ax, ax, 2        ; scale the subscript
    mov     bx, ax
    mov     ax, [a+bx]
    push    ax
    mov     ax, [s]
    pop     bx
    add     ax, bx
    mov     [s], ax
    inc     word ptr [i]
    jmp     Top
Done:
```

## With the optimizer

Six instructions, no memory traffic except the array element itself:

```asm
    mov     si, 0000h        ; counter
    lea     bx, [a]          ; element pointer
    xor     di, di           ; accumulator
Top:
    cmp     si, [limit]
    jg      Done
    add     di, [bx]         ; fused memory-operand ALU op
    add     bx, 2            ; step the pointer
    add     si, 1
    jmp     Top
Done:
```

This is what a person writes by hand.

## Equivalent BASIC

```basic
DIM p AS INTEGER PTR
p = VARPTR(a%(0))
FOR i% = 0 TO 999
  s% = s% + @p
  p = p +* 1            ' pb36 scaled pointer step
NEXT
```

## Why it is safe

The gates are deliberately narrow: `$OPTIMIZE SPEED`, a constant `STEP`, no
bounds/overflow/numeric checking (a check must fire per element, in order), no
aliasing and no calls in the body. Where the pattern does not match exactly, the
ordinary subscript path runs. Where both this and register residency could
apply, pointer stepping is tried **first**, because it removes the scale
entirely where a resident counter would still have to compute the address.

## Limits

Multiple arrays in one body, non-counter index strides and rank > 1 remain
open.
