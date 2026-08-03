# O0159 — Return-value propagation

| | |
|---|---|
| **Status** | ⬜ Planned (a fully-interpretable pure call already folds — [O0025](O0025-pure-function-folding.md)) |
| **Stage** | Whole-program analysis |
| **Related** | [O0025](O0025-pure-function-folding.md), [O0158](O0158-interprocedural-range-propagation.md), [O0161](O0161-function-summaries.md) |

## The idea

Facts flow **out** of a procedure as well as in. Three useful ones:

1. a `FUNCTION` that returns the same constant on every path is that constant at
   every call site — even when its body is not interpretable in general (which
   is what [O0025](O0025-pure-function-folding.md) requires);
2. its **range** and **known bits** propagate: a function returning
   `x AND 15` is in `0..15` whatever `x` is, so a caller can drop a bounds check
   on the result;
3. a function returning a **condition** can, with the right internal ABI, leave
   its answer in the flags instead of materializing −1/0
   ([O0169](O0169-returned-condition-propagation.md)).

## Applies to

```basic
FUNCTION Clamp15%(BYVAL v%)
  Clamp15% = v% AND 15
END FUNCTION

$ERROR BOUNDS ON
DIM t%(0 TO 15), n%
t%(Clamp15%(n%)) = 1          ' provably in range, but the check is emitted
```

## Today

The result is unknown at the call site, so the subscript keeps its Error-9 check.

## Planned

`Clamp15%` is summarized as returning `[0,15]` with the high bits clear, and the
check disappears.

## What it needs

- A **summary** per procedure ([O0161](O0161-function-summaries.md)): return
  range, known bits, constant-ness, purity — computed once and reused at every
  call site.
- Recursion needs a fixpoint over the call graph, with the same greatest-fixed-
  point treatment `ClassifyPure` already uses for purity.
