# O0201 — Fully-inlined procedure purge

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Whole-program, end of the IR middle end |
| **Source** | `Ir/Passes/GlobalDce.cs` — `Run`, called from `Ir/Passes/IrMiddleEndPipeline.cs` — `RunNativeModule` |
| **Gate** | `--optimize`, module owns every procedure's callers (`IrModule.OwnsProcedureAbi`) |
| **Split from** | [O0006](O0006-inlining.md) |

## What it is

A procedure inlined at **every** call site has no surviving real `CALL`, so its
body is dead weight in the image. `GlobalDce` removes every function with no
remaining call or taken address, to a fixpoint — which is why
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

The purge runs only when the module owns every caller of its procedures — not a
`$COMPILE UNIT`, no external calls, and every procedure lowered to IR — so no call
can come from outside what the pass sees. A taken address (`CODEPTR`) is a use
like a call, and a far-entry thunk's target is kept explicitly, so a procedure
that can still be reached indirectly keeps its body. The inliner never inlines
into or out of a procedure with an armed error handler, so those calls stay real.
It is the same pass and ownership rule as
[O0022](O0022-dead-procedure-elimination.md).
