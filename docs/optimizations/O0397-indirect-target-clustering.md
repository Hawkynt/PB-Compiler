# O0397 — Indirect target clustering

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0271](O0271-indirect-call-promotion.md), [O0279](O0279-whole-program-devirtualization.md), [O0398](O0398-branch-target-alignment.md) |

## The idea

The set of procedures reachable through a procedure pointer — the targets of a
dispatch table, a callback array, a delegate — are executed in alternation, so
they belong **together**. Clustering them improves instruction locality and, on
targets with a branch-target buffer, its hit rate.

The compiler already knows the candidate set: every address-taken procedure is
recorded in `CallBindings` for reachability
([O0022](O0022-dead-procedure-elimination.md)).

## Applies to

```basic
DIM handlers(0 TO 15) AS FUNCTION(LONG) AS LONG      ' pb36 delegate array
```

## What it needs

- The address-taken census, refined per **pointer variable** so that the
  candidates of one table cluster together rather than all address-taken
  procedures forming one blob ([O0279](O0279-whole-program-devirtualization.md)
  needs the same refinement).
- Placement weights from the profile, since a 16-entry table usually has two hot
  entries.
