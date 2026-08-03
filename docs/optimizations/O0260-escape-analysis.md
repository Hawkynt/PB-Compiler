# O0260 — Escape analysis

| | |
|---|---|
| **Status** | ⬜ Planned as a shared analysis (ad-hoc escape tests exist in [O0011](O0011-literal-overlap-pooling.md), [O0015](O0015-udt-zero-cost.md), [O0023](O0023-dead-global-elimination.md)) |
| **Stage** | Analysis infrastructure |
| **Related** | [O0059](O0059-scalar-replacement.md), [O0161](O0161-function-summaries.md), [O0286](O0286-allocation-elimination.md), [O0171](O0171-alias-analysis.md) |
| **Split from** | [O0161](O0161-function-summaries.md) |

## The idea

Prove that a value — an array, a string, a `TYPE` instance — **never escapes**
the procedure that creates it: its address is never taken, it is never passed
BYREF to an unproven callee, never stored into a global or a file, never named
in inline asm.

Almost every strong transformation downstream is really an escape question:
scalar replacement, allocation elimination, stack promotion, copy elision,
reference-count elision and literal packing each begin by asking it, and today
each answers it separately.

## Applies to

```basic
SUB Work
  LOCAL t AS Point           ' never escapes: eligible for everything
  LOCAL s$                   ' escapes only if passed out
  t.x = 1 : t.y = 2
  PRINT t.x + t.y
END SUB
```

## What it needs

- One reflective walk over the body (the instrument
  [O0022](O0022-dead-procedure-elimination.md) already uses) computing a
  per-value escape state, plus the callee summaries
  ([O0161](O0161-function-summaries.md)) to decide what a BYREF pass really does.
- PB's escape surface is **small and explicit** — `VARPTR`/`VARSEG`/`STRPTR`,
  BYREF, `DIM … AT`, `FIELD`, file `GET`/`PUT`, inline asm, external calls —
  which is what makes the analysis tractable here.
