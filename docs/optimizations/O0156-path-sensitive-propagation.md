# O0156 — Path-sensitive value propagation

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | SSA mid-end |
| **Related** | [O0016](O0016-value-fact-analysis.md), [O0045](O0045-ir-correlated-value-propagation.md), [O0107](O0107-branch-folding-through-phi.md) |

## The idea

At a merge, the lattice joins the incoming states and usually loses everything:
`x ∈ [0,9]` on one path and `x ∈ [100,109]` on the other becomes `[0,109]`, and
a later test against 50 can no longer be decided. Keeping the states **per
path** — up to a budget — preserves the facts that the join destroys.

The interval domain already refines per arm and joins at the merge
([O0016](O0016-value-fact-analysis.md)); this is about *not* joining where the
distinction still pays.

## Applies to

```basic
DIM mode%, x%
IF mode% THEN x% = 5 ELSE x% = 100
' ...
IF x% < 50 THEN PRINT "low" ELSE PRINT "high"     ' decidable per path
```

## Today

`x%` joins to `[5,100]`, so the second test is emitted and branched on.

## Planned

Each path keeps its own state; the second test folds on both, and with
[O0107](O0107-branch-folding-through-phi.md) the arms merge into the first `IF`.

## What it needs

- A **budget**: unbounded path sensitivity is exponential. The usual answer is a
  small number of tracked predicates per variable, plus a merge when the budget
  is exceeded.
- Interaction with the loop fixpoint: a path-sensitive state inside a loop must
  still converge, so the widening has to apply per path.
- The cheaper 80 % of this is [O0107](O0107-branch-folding-through-phi.md)
  (duplicate the join where the branch becomes decidable), which gets the same
  result structurally instead of analytically.
