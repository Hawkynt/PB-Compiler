# O0407 — Dead loop elimination

| | |
|---|---|
| **Status** | ✅ Implemented (IR only) |
| **Stage** | Mid-end |
| **Source** | — (no direct-emitter equivalent; this is IR-native) |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` |
| **IR** | ✅ `Ir/Passes/DeadLoopElimination.cs`, in `IrPassManager.Standard(optimizeForSpeed: true)` right after `closed-form` |
| **Verified by** | `DeadLoopEliminationTests` (both sides of the gate), `OptimizeSpeedCorpusTests` (whole corpus, SPEED off vs on) |
| **Related** | [O0134](O0134-recurrence-shortening.md), [O0132](O0132-compile-time-loop-evaluation.md), [O0002](O0002-dead-code-elimination.md) |

## The idea

[O0134](O0134-recurrence-shortening.md) answers what a loop produced without
running it. It does not remove the loop, and it cannot: closing the accumulator
is a statement about one value, while deleting the loop is a statement about
every value in it. So the two run in sequence, and this one collects what the
first left behind.

## Applies to

```basic
DIM i%, t%
FOR i% = 1 TO 400
  t% = t% + 2
NEXT
PRINT t%
```

## Today (without this pass)

`t%` closes to `800` and lands in the exit block — and then the loop still runs
four hundred times, incrementing a counter and an accumulator that nothing will
ever read.

## With it

```
PRINT 800
```

## Why it is safe

Three conditions, none of them droppable:

- **The trip count must be known and finite.** Deleting a loop that never
  terminates replaces a program that hangs with one that does not. Nobody wanted
  the hang, but it is still the program's behaviour, and this pass does not get
  to overrule it. The count is found by the same simulation
  [O0134](O0134-recurrence-shortening.md) uses — shared, in `CountedLoop`, so
  the pass that deletes and the pass that replaces cannot disagree about what
  the loop is.
- **The body writes nothing and calls no one.** `IrStore`, `IrCall` and
  `IrInlineAsm` are the effects observable output is made of; a region
  containing any of them is left alone.
- **Nothing defined inside is read outside.** The counter's exit value is
  `limit + step` and could perfectly well be computed here — but computing it is
  [O0134](O0134-recurrence-shortening.md)'s job, and declining until that has
  happened is what keeps the two passes from having to agree about arithmetic as
  well as about shape.

## Why the `$OPTIMIZE SPEED` gate

A DOS-era **delay loop** is exactly this shape written on purpose:

```basic
FOR i% = 1 TO 30000 : NEXT       ' wait a moment
```

It has no effect the IR can see and every effect the author wanted. Deleting it
preserves every printed byte and destroys the program.

PB spells the intent `SLEEP` and `DELAY`, so under `$OPTIMIZE SPEED` the
busy-wait is taken to be an accident and goes; under `$OPTIMIZE SIZE` it is left
alone. This is the first transform in the middle end whose licence comes from the
optimization *mode* rather than from the IR alone, which is why
`IrPassManager.Standard` gained a parameter for it and why
`OptimizeSpeedCorpusTests` exists — a pass that only runs under a flag is a pass
the rest of the suite never sees.

## Limits

- A loop containing **any** call is declined, including a call to a function the
  module already knows is pure. Purity is a module-level fact
  (`FunctionSummaries`) and this is a function pass; wiring the two together is
  [O0132](O0132-compile-time-loop-evaluation.md)'s problem, not this one's.
- A loop whose counter is read afterwards survives until
  [O0134](O0134-recurrence-shortening.md) learns to close the counter as well as
  the accumulator — it currently skips it, because the loop's own test counts as
  a reader.
- Nested loops are deleted innermost-first, one fixpoint sweep per level.
