# O0291 — Handle ownership elision

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0179](O0179-string-self-assignment.md), [O0296](O0296-string-move-instead-of-copy.md), [O0260](O0260-escape-analysis.md) |

## The idea

The string manager's discipline is: assigning a value **duplicates** it and
frees the old handle; leaving scope **frees** it. Matching dup/free pairs that
provably bracket no observable use cancel out and can both be removed — the
BASIC equivalent of eliding a reference-count increment and its decrement.

## Applies to

```basic
DIM a$, b$
a$ = b$                      ' dup b$, free a$'s old handle, store
PRINT a$
' a$ dies here: free
```

If `b$` itself is dead after the assignment, the whole dup/free pair is a copy
of a value that was about to be destroyed —
[O0296](O0296-string-move-instead-of-copy.md) turns it into a move.

## What it needs

- **Lifetime analysis over handles**, which is the piece the string runtime does
  not have today: it is a manual dup/free protocol emitted by codegen, with no
  central model of who owns what.
- Exactness is non-negotiable: an elided free is a leak and a doubled free is a
  corrupted heap, so the analysis has to be conservative by construction —
  eliding only where both sides of the pair are visible in the same body.
