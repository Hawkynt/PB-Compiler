# O0388 — Tail-call layout

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0213](O0213-cross-procedure-tail-call.md), [O0385](O0385-cross-function-fallthrough.md), [O0381](O0381-branch-distance-minimization.md) |

## The idea

A tail call is a jump ([O0213](O0213-cross-procedure-tail-call.md)), so its
target should be **near**: a short jump instead of a near one, and — in the best
case — no jump at all
([O0385](O0385-cross-function-fallthrough.md)).

Mutual recursion makes this concrete: two procedures that tail-call each other
form a loop across a procedure boundary, and laying them adjacent turns that
loop into two short backward jumps rather than two long ones.

## Applies to

```basic
SUB Even(BYVAL n%)
  IF n% = 0 THEN EXIT SUB
  CALL Odd(n% - 1)
END SUB
SUB Odd(BYVAL n%)
  IF n% = 0 THEN EXIT SUB
  CALL Even(n% - 1)
END SUB
```

## What it needs

- Recognition that the edge is a tail call, which the emitter already
  determines ([O0213](O0213-cross-procedure-tail-call.md)) and would have to
  record for the layout stage.
- Distance-aware placement ([O0381](O0381-branch-distance-minimization.md)) with
  tail edges weighted like loop back edges — because that is what they are.
