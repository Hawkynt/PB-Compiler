# O0132 — Whole-loop compile-time evaluation

| | |
|---|---|
| **Status** | ⬜ Planned (pure *function* calls with constant arguments already fold — [O0025](O0025-pure-function-folding.md)) |
| **Stage** | Mid-end |
| **IR** | ✅ falls out of `LoopUnroll` + `Sccp` + `Dce` composing — a constant-trip loop is unrolled, its counter becomes a constant in each copy, the arithmetic folds and the dead copies go. `FOR i = 1 TO 5 / s = s + i / NEXT` becomes `PRINT 15`. Pinned by `LoopUnrollTests` |
| **Related** | [O0025](O0025-pure-function-folding.md), [O0020](O0020-idiom-replacement.md), [O0133](O0133-loop-prefix-evaluation.md) |

## The idea

[O0025](O0025-pure-function-folding.md) already contains a tree-walking
interpreter with step and recursion budgets, and it *does* execute `FOR`, `DO`
and `WHILE` — but only inside a pure `FUNCTION` called with constant arguments.
The same interpreter should run a **top-level** loop whose every input is known
and whose every effect is confined to compile-time-known variables.

## Applies to

```basic
DIM i%, t%(0 TO 15)
FOR i% = 0 TO 15
  t%(i%) = i% * i%          ' a lookup table, computed at run time today
NEXT
```

## Today

16 iterations at run time, plus the array's zero fill, to produce a table that
never changes.

## Planned

The loop is executed at compile time and the array is emitted as initialized
data:

```
Data
  t:  dw 0,1,4,9,16,25,36,49,64,81,100,121,144,169,196,225
```

## Equivalent BASIC

```basic
DIM t%(0 TO 15)
' t%() is initialized in the image; no loop runs
```

## What it needs

- Extending `OptPureFold`'s evaluator to **arrays and locals at module scope**,
  with the same wrap-exact `WrapToType` discipline at every operation width.
- A **budget** (the existing 500 000-step / 64-deep limits) and a size limit on
  the data produced — a loop that fills a 64 KB array is not a win.
- Interaction with [P0003](P0003-bss.md): a computed table is initialized data,
  so it costs image bytes that a zero-filled array does not. The cost model
  decides which is smaller.
