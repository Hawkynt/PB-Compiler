# O0166 — Dead call-result elimination

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter / mid-end |
| **Related** | [O0025](O0025-pure-function-folding.md), [O0002](O0002-dead-code-elimination.md), [O0161](O0161-function-summaries.md) |

## The idea

A call to a **pure** function whose result nobody uses does nothing at all — the
call, its argument evaluation and the result handling are all dead.

Purity is already inferred for the whole call graph
([O0025](O0025-pure-function-folding.md)); what is missing is using that fact
for calls with non-constant arguments, where folding is impossible but deletion
is not.

## Applies to

```basic
FUNCTION Hash%(BYVAL v%)      ' inferred pure
  Hash% = (v% * 31) XOR 17
END FUNCTION

DIM n%, t%
t% = Hash%(n%)                ' t% is never read afterwards
PRINT n%
```

## Today

The call runs, the frame is built, the result is stored to `t%` — and the store
is only removed by [O0002](O0002-dead-code-elimination.md) if the right-hand
side is a literal or a copy, which a call is not.

## Planned

The store and the call both disappear; with no callers left,
[O0022](O0022-dead-procedure-elimination.md) purges `Hash%` from the image.

## What it needs

- Extending the dead-store rule to accept a **pure call** as a removable
  right-hand side — the purity classifier already answers the question, and the
  argument expressions must be checked for effects independently.
- Under `$ERROR` modes, a pure function can still **trap** (Error 6 on overflow,
  Error 11 on a divide) — so removal needs either a no-trap proof or the checks
  to be off, the same condition [O0023](O0023-dead-global-elimination.md) uses.
