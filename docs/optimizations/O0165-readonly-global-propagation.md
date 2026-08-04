# O0165 — Read-only global propagation

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Whole-program analysis |
| **IR** | ✅ `Ir/Passes/ReadOnlyGlobals.cs` — a module-level variable nothing ever writes reads as ZERO, which is what PB guarantees an uninitialized variable holds (the same rule `Mem2Reg` uses for a promoted slot with no reaching store). Every use must be a plain load: an address that reaches a call, a store or a GEP means the writes are not all visible, so it declines. Registered with `AddModulePass`; deleting the now-unused global is [O0023](O0023-dead-global-elimination.md)'s job. Verified by `ReadOnlyGlobalsTests` and `IrPassObservableEquivalenceTests` |
| **Related** | [O0023](O0023-dead-global-elimination.md), [O0017](O0017-sccp.md), [O0161](O0161-function-summaries.md) |

## The idea

An internal global with **one constant initializer and no writes** is a
constant, whatever it was declared as. Every read folds to the literal, the data
slot disappears ([O0023](O0023-dead-global-elimination.md)), and the folds
cascade into branch elimination and dead code.

DOS-era BASIC uses `DIM SHARED` where a modern program would use `CONST`,
usually because the value was once configurable — so this pattern is
everywhere in the corpus.

## Applies to

```basic
DIM SHARED ScreenWidth%
ScreenWidth% = 320            ' the only assignment in the program

SUB Plot(BYVAL x%, BYVAL y%)
  DIM o&
  o& = y% * ScreenWidth% + x%
  ...
END SUB
```

## Today

Every use is a memory read, and `y% * ScreenWidth%` is a general multiply
because the operand is not a literal.

## Planned

`ScreenWidth%` folds to 320 everywhere; the multiply becomes the shift chain
[O0004](O0004-strength-reduction.md) already emits for `* 320`, and the global's
slot and its store disappear.

## What it needs

- The **write classification** already implemented in
  [O0023](O0023-dead-global-elimination.md), inverted: instead of "no reads", the
  question is "no writes after the initializing one".
- The same conservative guards: address taken, `SHARED`/`COMMON` exposure to a
  linked unit, `DIM … AT`, inline asm, or a write on any path (including an
  error handler) keeps the variable as it is.
- Self-contained main only, exactly like the dead-global pass.
