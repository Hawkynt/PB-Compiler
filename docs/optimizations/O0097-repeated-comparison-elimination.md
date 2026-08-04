# O0097 — Repeated comparison elimination

| | |
|---|---|
| **Status** | ✅ Implemented via the range lattice — testing the same condition twice on one path folds. Inside `IF x < 10 THEN`, the lattice refines `x` to `[..,9]`, so a nested `IF x < 10` is proven always-true (its compare eliminated) and `IF x >= 10` always-false (its arm dropped). Verified in the emitted x86 (one `CMP` where the source has three) |
| **Stage** | Mid-end (dominator-scoped) |
| **IR** | ✅ `Ir/Passes/Gvn.cs` — the value table keys `IrCmp` by predicate and operands (commutative predicates ordered), scoped to the dominator tree, so a comparison recomputed anywhere the first one dominates is replaced by it rather than re-evaluated. This is the dominator-scoped half; the range-lattice half that PROVES a repeated test is `CorrelatedValueProp` |
| **Related** | [O0016](O0016-value-fact-analysis.md), [O0045](O0045-ir-correlated-value-propagation.md), [O0081](O0081-flag-reuse.md) |

## The idea

Testing the same unchanged condition twice along one path is pure waste. Inside
the region dominated by `IF x < 10 THEN`, the fact `x < 10` holds — so a second
`IF x < 10` there is `TRUE` and a `IF x >= 10` is `FALSE`, both foldable without
any arithmetic.

The value lattice ([O0016](O0016-value-fact-analysis.md)) already refines ranges
per arm, and the IR tier already propagates equality facts
([O0045](O0045-ir-correlated-value-propagation.md)). What is missing is the
comparison-level fact: remembering the *predicate*, not only the resulting
interval.

## Applies to

```basic
DIM x%
IF x% < 10 THEN
  CALL Log("small")
  IF x% < 10 THEN PRINT "still small"    ' provably TRUE
END IF
```

## Today

The second comparison is emitted and branched on.

## Planned

The inner `IF` is folded to its taken arm and the compare disappears — and with
[O0002](O0002-dead-code-elimination.md), so does any arm it makes unreachable.

## What it needs

- A **predicate environment** carried alongside the interval environment, keyed
  by (lvalue, operator, operand) and invalidated by any write to either side —
  including through the call, which today already invalidates only what a callee
  can reach ([O0016](O0016-value-fact-analysis.md)).
- Relational facts between two *variables* (`x < y`) need
  [O0157](O0157-relational-range-propagation.md); the constant-operand case
  needs only what exists.
