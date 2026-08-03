# O0063 — Duff's-device unrolling (variable-trip loops)

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0007](O0007-loop-unrolling.md), [O0026](O0026-auto-vectorization.md), [O0066](O0066-unrolled-counter-propagation.md) |

## The idea

[O0007](O0007-loop-unrolling.md) only unrolls loops whose trip count is a
compile-time constant. A **variable**-trip loop can be unrolled too: emit the
body N times and enter the chain at offset `count MOD N` through a computed
jump, so no scalar prologue loop is needed. That is Duff's device, expressed
directly in the emitted assembler.

It pays exactly where `REP` string operations cannot express the body — planar
masks, per-element transforms, strided writes.

## Applies to

```basic
$OPTIMIZE SPEED
DIM i%, n%, a%(0 TO 999)
FOR i% = 0 TO n%
  a%(i%) = a%(i%) * 3
NEXT
```

## Today

One iteration per element: compare, body, increment, back-edge.

## Planned (factor 4)

```asm
    mov     cx, [n]
    inc     cx
    mov     ax, cx
    and     ax, 0003h        ; count MOD 4 selects the entry point
    shl     ax, 1
    mov     bx, ax
    jmp     word ptr [EntryTable+bx]
Body4:  ...                  ; element i+3
Body3:  ...                  ; element i+2
Body2:  ...                  ; element i+1
Body1:  ...                  ; element i
    add     bx, 8
    sub     cx, 4
    ja      Body4
```

## Equivalent BASIC

```basic
FOR i% = 0 TO n% STEP 4
  a%(i%) = a%(i%) * 3 : a%(i%+1) = a%(i%+1) * 3
  a%(i%+2) = a%(i%+2) * 3 : a%(i%+3) = a%(i%+3) * 3
NEXT
' plus the entry offset that handles the remainder up front
```

## What it needs

- An unroll-factor policy (`$OPTIMIZE UNROLL n`) and a code-size budget.
- The PB **increment-then-test counter end value** (QUIRK 2.28) must survive
  exactly, including the 16-bit wrap, which is the fiddly part with a computed
  entry.
- A zero-trip guard: with `n% < 0` the loop body must not run at all.
