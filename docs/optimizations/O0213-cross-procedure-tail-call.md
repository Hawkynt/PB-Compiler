# O0213 — Cross-procedure tail call

| | |
|---|---|
| **Status** | ✅ Implemented (`SUB` → `SUB`) |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.cs`, `CodeGenerator.Procs.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF87.BAS` (mutual recursion, differing argument counts, a deliberately non-tail call) |
| **Split from** | [O0014](O0014-tail-call-optimization.md) (which is now the self-call form) |

## What it is

A `SUB A` whose last action is `CALL B(args)` — B another in-module `SUB` — tears
down A's frame, lays B's whole call frame (return address + arguments) at A's
caller's pre-call SP boundary, and **jumps** to B's entry. B's own `RET nb` then
returns straight to A's caller: one frame and one return removed per tail call,
so mutual recursion runs in constant stack.

The teardown accounts for A's and B's argument-byte counts **independently** —
B's return-address slot lands at `[BP+2+(na-nb)]` — so the callee-cleans `RET n`
discipline stays balanced even when A and B take different argument bytes.

## Sample

```basic
SUB Even(BYVAL n%)
  IF n% = 0 THEN PRINT "even" : EXIT SUB
  CALL Odd(n% - 1)           ' tail position
END SUB

SUB Odd(BYVAL n%)
  IF n% = 0 THEN PRINT "odd" : EXIT SUB
  CALL Even(n% - 1)
END SUB
```

## Why it is safe

The gates of [O0014](O0014-tail-call-optimization.md) apply, plus two more:

- B must be a **defined in-module `SUB`** — a known local jump target;
- it is **not** applied to `FUNCTION`s: a function's result-load epilogue, and a
  discarded result's `StrFree` or FPU pop, must still run, so those fall back to
  an ordinary `CALL`.
