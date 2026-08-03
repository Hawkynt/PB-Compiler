# O0332 — Lookup-table generation

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0025](O0025-pure-function-folding.md), [O0132](O0132-compile-time-loop-evaluation.md), [O0333](O0333-lookup-table-elimination.md) |

## The idea

A **pure** function over a **small domain** can be evaluated at compile time for
every input and emitted as a table. The call becomes an indexed load.

The purity analysis and the interpreter already exist
([O0025](O0025-pure-function-folding.md)); what is missing is the decision to
run them over the whole domain rather than at one constant call site.

## Applies to

```basic
FUNCTION SinTab%(BYVAL deg%)        ' pure, domain 0..359
  SinTab% = INT(SIN(deg% * 3.14159 / 180) * 1024)
END FUNCTION

PRINT SinTab%(angle%)               ' any angle: a call today
```

## What it needs

- A **domain bound** — from the parameter's proven range
  ([O0158](O0158-interprocedural-range-propagation.md)) or a declaration — and a
  size budget: 360 words is an obvious win, 65 536 words is not.
- Extending the evaluator past its integer-only subset if the function uses
  floats, with the same bit-exactness discipline.
- The reverse trade is real too, and target-dependent
  ([O0333](O0333-lookup-table-elimination.md)).
