# O0056 — Reciprocal-multiply division by a constant

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter (would extend [O0004](O0004-strength-reduction.md)) |
| **Related** | [O0004](O0004-strength-reduction.md), [R0003](R0003-string-engine.md) |

## The idea

Division by a power of two already lowers to a shift. Division by any *other*
compile-time constant can be lowered too, with the standard magic-number trick:
multiply by a fixed-point reciprocal and shift the high half down. On an 8086,
where `DIV`/`IDIV` costs ~80–160 cycles against `MUL`'s ~120 for a full 16×16
product plus a couple of shifts, the win is real; on a 386+ it is decisive.

It pairs naturally with a two-digit-table number formatter (see
[R0003](R0003-string-engine.md)), which is the biggest consumer of
divide-by-ten in PRINT-heavy code.

## Applies to

```basic
DIM n%, d%, r%
d% = n% \ 10
r% = n% MOD 10
```

## Today

```asm
    mov     ax, [n]
    mov     bx, 000Ah
    or      bx, bx
    jz      rt_err_div
    cwd
    idiv    bx               ; ~100+ cycles
    mov     [d], ax
```

## Planned

```asm
    mov     ax, [n]
    mov     dx, 6667h        ; magic reciprocal for 10
    imul    dx               ; DX:AX = n * magic
    sar     dx, 2
    mov     ax, [n]
    sar     ax, 15           ; the sign correction
    sub     dx, ax
    mov     [d], dx
```

## Equivalent BASIC

```basic
d% = (n% * 26215&) \ 262144 - SGN_CORRECTION
```

## What it needs

- Magic-number selection per divisor and per width (16- and 32-bit, signed and
  unsigned), with the standard proof that the chosen (multiplier, shift) pair is
  exact over the whole input range.
- Agreement with PB's truncate-toward-zero `\` and dividend-signed `MOD`.
- The `$ERROR` interaction is already settled by
  [O0004](O0004-strength-reduction.md): a non-zero constant divisor can raise
  neither Error 11 nor a quotient overflow.
