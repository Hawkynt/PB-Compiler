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

## Attempted 2026-08-06 — the wiring is not enough

The premise checks out: `OptPureFold.ClassifyPure` really does compute a
call-graph purity fixed point, and it is trusted far enough to EXECUTE those
bodies at compile time and fold the call to a literal, so believing it in order to
merely *delete* a call is the lesser step. `t% = Hash%(7)` folds to `t = 200`,
confirming `Hash%` is classified pure.

`DeadStore` also already has the liveness machinery, and its only gate looked like
the one thing missing — `IsRemovableRhs` knows literals, equates and variable
copies, and any call stops it.

Wiring those two together **does not work**, and the reason is upstream of both.
Exposing the pure set, passing it into `DeadStore.Compute` and admitting a pure
`CallOrIndexExpr` in `IsRemovableRhs` leaves the image byte-for-byte unchanged.
Bisected by dropping the purity test from the pattern entirely (`|| true`): still
unchanged, so the arm never matches at all — this is not about which procedures
are considered pure.

What is ruled out, so the next attempt need not re-do it:

- the pure set is computed and non-null at the call site;
- dead-store elimination *is* reached for this program shape and does remove a
  plain dead store (`t = 5` next to the identical program without it produces the
  same image, byte for byte);
- `t` is a trackable scalar, and `FindTrackable` escapes only a call's ARGUMENTS,
  not an assignment's target;
- and most directly: in the SAME program, with the same escaped `n`, an unused copy
  `t = n` **is** removed (byte-identical to the program without it). So candidacy,
  liveness and statement removal all work for this shape — a literal RHS and a copy
  RHS both go, and only the call-valued one stays.

Three RHS shapes in one program shape, `t` unused in all three:

| RHS | removed? |
|---|---|
| `t = 5` | yes |
| `t = n` | yes |
| `t = Hash%(n)` | no — even with the purity test bypassed |

So the assignment whose RHS is a user call does not reach `DeadStore` as an
`SsaDefKind.Assign` candidate in the first place. That is a question about how
`SsaForm` builds defs for call-valued assignments, and it is where this should
resume — not in `IsRemovableRhs`, which is where it looks like it should live.
