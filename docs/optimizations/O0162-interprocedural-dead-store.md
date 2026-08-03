# O0162 — Interprocedural dead-store elimination

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Whole-program analysis |
| **Related** | [O0002](O0002-dead-code-elimination.md), [O0023](O0023-dead-global-elimination.md), [O0161](O0161-function-summaries.md) |

## The idea

A store is dead if nothing reads it before it is overwritten — a question that
today stops at the procedure boundary. With mod/ref summaries
([O0161](O0161-function-summaries.md)) it can be answered across calls:

- a store to a global that **every** reachable callee overwrites before reading
  is dead;
- a store into a `BYREF` argument slot that the callee never reads is dead;
- an argument evaluated only to be ignored need not be evaluated
  ([O0069](O0069-dead-parameter-elimination.md)).

## Applies to

```basic
DIM SHARED buffer%

SUB Reset
  buffer% = 0                 ' overwrites unconditionally, reads nothing first
END SUB

buffer% = ComputeExpensive%   ' dead: Reset overwrites it before any read
CALL Reset
PRINT buffer%
```

## Today

The store is kept, because a call is an opaque barrier for the dead-store
analysis.

## Planned

`Reset`'s summary says it writes `buffer%` without reading it, so the earlier
store is dead — and if `ComputeExpensive%` is pure, its call dies too
([O0166](O0166-dead-call-result-elimination.md)).

## What it needs

- [O0161](O0161-function-summaries.md), and specifically the *must-write*
  (not merely may-write) part of mod/ref, which is the stronger and rarer fact.
- The same trap guard [O0023](O0023-dead-global-elimination.md) uses: a store
  whose right-hand side could raise Error 6/9/11 under `$ERROR` may not be
  dropped, because the trap is observable.
