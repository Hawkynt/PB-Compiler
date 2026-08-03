# O0261 — Termination analysis

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Analysis infrastructure |
| **Related** | [O0161](O0161-function-summaries.md), [O0166](O0166-dead-call-result-elimination.md), [O0132](O0132-compile-time-loop-evaluation.md) |
| **Split from** | [O0161](O0161-function-summaries.md) |

## The idea

"Does this call always return?" is a precondition several transformations
quietly need. Removing a pure call whose result is unused
([O0166](O0166-dead-call-result-elimination.md)) is only sound if the call
**terminates** — otherwise the program's observable behavior (hanging) changes.
The same applies to sinking a call into a branch and to evaluating a loop at
compile time.

## Applies to

```basic
FUNCTION Spin%(BYVAL n%)     ' pure, but may not terminate
  DO WHILE n% > 0
    ' n% is never decremented
  LOOP
  Spin% = 0
END FUNCTION

DIM t%
t% = Spin%(1)                ' removing this call would change the program
```

## What it needs

- A simple, conservative proof: bounded loops with monotone counters terminate;
  recursion with a decreasing measure terminates
  ([O0168](O0168-recursive-argument-evolution.md)); everything else is "unknown",
  which blocks the transformations that need the fact.
- Storage in the per-procedure summary ([O0161](O0161-function-summaries.md)).
