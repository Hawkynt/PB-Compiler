# O0079 — Quotient and remainder share one divide

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0003](O0003-common-subexpression-elimination.md), [O0004](O0004-strength-reduction.md), [O0056](O0056-reciprocal-division.md) |

## The idea

`IDIV` leaves the quotient in AX **and** the remainder in DX. When a program
needs both `n \ d` and `n MOD d` with the same operands, one divide suffices —
and on an 8086 a 16-bit `IDIV` costs ~100–180 cycles, so the second one is by
far the most expensive redundancy in the statement.

## Applies to

```basic
DIM n%, d%, q%, r%
q% = n% \ d%
r% = n% MOD d%
```

## Today

Two full divides, each with its own divide-by-zero guard:

```asm
    mov     ax, [n]
    mov     bx, [d]
    or      bx, bx
    jz      rt_err_div
    cwd
    idiv    bx
    mov     [q], ax
    mov     ax, [n]          ; and again
    mov     bx, [d]
    or      bx, bx
    jz      rt_err_div
    cwd
    idiv    bx
    mov     [r], dx
```

## Planned

```asm
    mov     ax, [n]
    mov     bx, [d]
    or      bx, bx
    jz      rt_err_div
    cwd
    idiv    bx
    mov     [q], ax
    mov     [r], dx
```

## Equivalent BASIC

```basic
q% = n% \ d%
r% = n% - q% * d%        ' the same value, without the second divide
```

## What it needs

- Recognition at the **statement-run** level, which is where
  [O0003](O0003-common-subexpression-elimination.md) already caches values —
  this is the same machinery with a two-result definition (AX and DX) instead of
  one.
- Invalidation on any write to `n` or `d` between the two, and a barrier check
  identical to the existing CSE cache.
- The Error-11 guard must fire exactly once, where the first divide was.
