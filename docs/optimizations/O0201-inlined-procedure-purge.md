# O0201 — Fully-inlined procedure purge

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Whole-program, before emission |
| **Source** | `CodeGen/OptInlining.cs` — `FullyInlinedProcedures` |
| **Gate** | `--optimize`, self-contained main |
| **Split from** | [O0006](O0006-inlining.md) |

## What it is

A procedure inlined at **every** call site has no surviving real `CALL`, so its
body is dead weight in the image. The reachability purge drops it — which is why
inlining a small helper is a size *win* rather than a trade.

## Sample

```basic
FUNCTION Twice%(BYVAL v%)
  Twice% = v% * 2
END FUNCTION

PRINT Twice%(3); Twice%(4)     ' both call sites inline
```

## Result

```
Procedures
  (none)
```

## Why it is safe

The purge fires only for a **self-contained main**, and bails the moment a
procedure's address is taken (`CODEPTR`) or the program uses any error handling —
either of which can force a real call that must keep the body. It shares the
ownership rule with [O0022](O0022-dead-procedure-elimination.md), whose
reachability walk is complete by construction.
