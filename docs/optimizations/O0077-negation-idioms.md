# O0077 — Negation idioms

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0076](O0076-algebraic-identities.md), [O0004](O0004-strength-reduction.md), [O0033](O0033-constant-store.md) |

## The idea

Three rewrites around unary minus:

| Source | Becomes |
|---|---|
| `0 - x` | `NEG` |
| `x * -1` | `NEG` |
| `-(-x)` | `x` |

## Applies to

```basic
DIM x%, a%, b%, c%
a% = 0 - x%
b% = x% * -1
c% = -(-x%)
```

## Today

`0 - x%` loads the zero, stages it, and subtracts; `x% * -1` runs a multiply (or,
under `$OPTIMIZE SPEED`, a shift chain with a trailing `NEG`); the double
negation emits two negations.

## Planned

```asm
    mov     ax, [x]
    neg     ax
    mov     [a], ax
    mov     ax, [x]
    neg     ax
    mov     [b], ax
    mov     ax, [x]
    mov     [c], ax
```

## What it needs

- **`-32768` is the whole problem.** `NEG` of `8000h` yields `8000h` and sets
  OF. PB's own negation is float-promoted, so `m% = -32768` stores `8000h`
  ([O0033](O0033-constant-store.md) already reproduces that); the rewrite must
  land on the same bits and must keep the Error-6 trap under `$ERROR OVERFLOW`.
- The same review for `LONG` (`80000000h`) and for the unsigned types, where
  `NEG` is a modular complement rather than an arithmetic negation.
