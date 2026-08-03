# O0354 — Equality saturation

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0043](O0043-ir-instcombine.md), [O0061](O0061-reassociation.md), [O0355](O0355-superoptimized-peepholes.md), [O0174](O0174-target-cost-models.md) |

## The idea

Rewrite rules applied in sequence are **order-dependent**: applying one can
destroy the opportunity for another, and the peephole pass has to guess a good
order. An **e-graph** avoids the guess by representing *all* equivalent forms of
an expression at once, saturating them with the rewrite rules, and then
extracting the cheapest form under the target cost model.

## Applies to

```basic
DIM x%, r%
r% = (x% * 2 + x%) \ 3       ' = x%; only visible if the rules apply in one order
```

## What it needs

- An e-graph implementation and a rule set — the rules themselves already exist
  scattered across [O0043](O0043-ir-instcombine.md),
  [O0076](O0076-algebraic-identities.md),
  [O0004](O0004-strength-reduction.md) and the peephole.
- A **cost function**, which is exactly [O0174](O0174-target-cost-models.md):
  extraction is only as good as the cost model it optimizes against.
- Wrap-correctness on every rule, per dialect — the rewrite set is where the
  `32767 + 18` class of bug ([O0001](O0001-constant-folding.md)) would reappear
  at scale if the rules were stated naively.
