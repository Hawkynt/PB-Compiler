# O0213 — Cross-procedure tail call

| | |
|---|---|
| **Status** | 🟡 Partial — mutual tail recursion runs in constant stack; no general cross-procedure `JMP` |
| **Stage** | IR middle end |
| **Source** | `Ir/Passes/Inliner.cs` + `Ir/Passes/TailRecursion.cs`; no back-end tail jump to another procedure |
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

The frame-reusing jump described above was the retired direct emitter's; the IR
path has no cross-procedure tail jump. Mutual recursion still runs in constant
stack by a different route: the inliner turns `A calls B calls A` into a
self-call of A, and `TailRecursion` then rewrites that self tail call as a loop
(`Execute_GivenDeepMutualTailRecursion_WhenRouted_ThenConstantStack`). A tail call
to a procedure that is not inlined remains an ordinary `CALL`.

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

The inliner's gates of [O0006](O0006-inlining.md) and the self-call gates of
[O0014](O0014-tail-call-optimization.md) apply: the call must be in tail
position, no frame address may escape, and a procedure with an error handler or
inline assembly is left alone.
