# O0249 — Branchless absolute value

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0108](O0108-branchless-select.md), [O0077](O0077-negation-idioms.md), [O0258](O0258-vector-abs.md) |
| **Split from** | [O0108](O0108-branchless-select.md) |

## The idea

`IF x < 0 THEN x = -x` — and the `ABS()` intrinsic — lower to the classic
three-instruction sequence with no branch at all:

```asm
    cwd                      ; DX = sign mask
    xor     ax, dx
    sub     ax, dx
```

## Applies to

```basic
DIM x%
IF x% < 0 THEN x% = -x%
PRINT ABS(x%)
```

## What it needs

- The `-32768` case: its absolute value is not representable, and the branchless
  sequence returns `-32768` — which is what the branching form does too, so the
  rewrite is exact. Under `$ERROR OVERFLOW` the negation's trap must be
  preserved, which the mask form does **not** raise; that path keeps the branch.
- A recognizer for both spellings (the explicit `IF` and the intrinsic).
