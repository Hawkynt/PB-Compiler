# O0158 — Interprocedural range propagation

| | |
|---|---|
| **Status** | ⬜ Planned (constant-only propagation exists — [O0018](O0018-interprocedural-constant-propagation.md)) |
| **Stage** | Whole-program analysis |
| **Related** | [O0018](O0018-interprocedural-constant-propagation.md), [O0016](O0016-value-fact-analysis.md), [O0160](O0160-call-site-cloning.md) |

## The idea

[O0018](O0018-interprocedural-constant-propagation.md) requires every call site
to pass the **same constant**. That is a narrow condition — but the *union of
the argument ranges* over all call sites is almost always useful even when no
single constant exists.

If a procedure is only ever called with a first argument in `0..99`, its body
can drop bounds checks, narrow arithmetic and fold comparisons exactly as if the
range had been proven locally.

## Applies to

```basic
$ERROR BOUNDS ON
SUB Plot(BYVAL x%, BYVAL y%)
  DIM screen%(0 TO 199, 0 TO 319)
  screen%(y%, x%) = 15        ' checked, because x% and y% are unknown
END SUB

CALL Plot(10, 20)
CALL Plot(100, 50)
CALL Plot(319, 199)
```

## Today

The checks stay: no single call site value dominates.

## Planned

`x% ∈ [10,319]`, `y% ∈ [20,199]` from the three call sites — both inside the
array bounds, so both checks are dropped.

## What it needs

- The same **ownership** condition [O0018](O0018-interprocedural-constant-propagation.md)
  uses: every call site visible, no address taken. An unseen indirect call
  invalidates the whole range.
- The lattice already exists ([O0016](O0016-value-fact-analysis.md)); what is
  new is seeding a procedure's entry state from the join of its call sites, and
  iterating to a fixpoint over the call graph.
- Where the joined range is uselessly wide, the answer is
  [O0160](O0160-call-site-cloning.md) — clone rather than merge.
