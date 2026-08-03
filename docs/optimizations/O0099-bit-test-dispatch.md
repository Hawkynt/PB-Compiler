# O0099 — Bit-test dispatch for small constant sets

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0029](O0029-select-jump-table.md), [O0098](O0098-balanced-decision-tree.md), [O0032](O0032-short-circuit-conditions.md) |

## The idea

Membership in a small constant set — `CASE 1, 3, 5, 9` or
`IF k% = 1 OR k% = 3 OR k% = 5 THEN` — is a **bit mask** test, not a chain of
comparisons: build the constant mask at compile time, shift it by the value, and
test bit 0. On a 386+ the `BT` instruction does it directly.

## Applies to

```basic
DIM k%
IF k% = 1 OR k% = 3 OR k% = 5 OR k% = 9 THEN PRINT "odd-ish"
```

## Today

Four compares and four branches (after
[O0032](O0032-short-circuit-conditions.md) short-circuits them, which already
helps, but the worst case is still four).

## Planned

```asm
    mov     ax, [k]
    cmp     ax, 000Fh        ; range guard: the mask covers 0..15
    ja      NotMember
    mov     cx, ax
    mov     ax, 022Ah        ; bits 1,3,5,9 set
    shr     ax, cl
    test    ax, 0001h
    jz      NotMember
```

Constant time, six instructions, no branches until the answer.

## Equivalent BASIC

```basic
IF k% >= 0 AND k% <= 15 THEN
  IF ((&H022A \ 2 ^ k%) AND 1) THEN PRINT "odd-ish"
END IF
```

## What it needs

- The set must fit the mask width (16 bits natively, 32 under
  `$CPU 80386`), plus a range guard for values outside it.
- Recognition on both spellings: a `SELECT CASE` arm list and an `OR` chain of
  equality tests (the same recognizer
  [O0067](O0067-if-chain-jump-table.md) needs).
- A cost model call against the jump table: the mask needs no table bytes at
  all, so on a size-constrained target it wins even where a table would fit.
