# O0401 — Layout-aware inlining

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Whole-program |
| **Related** | [O0269](O0269-profile-guided-inlining.md), [O0006](O0006-inlining.md), [O0375](O0375-working-set-minimization.md) |

## The idea

The inliner's cost model counts call overhead against callee size. That is the
right model for a machine with infinite fetch bandwidth. On a real one, inlining
a body into ten call sites **grows the hot working set tenfold** — and can cost
more in fetch and page traffic than the ten calls it removed.

Including instruction-cache and page cost in the inline decision is what turns
"inline everything small" into a defensible policy.

## Applies to

Every inline decision — and especially the trivial-method inlining
([O0200](O0200-trivial-method-inlining.md)) that `pb36`'s object model relies
on, where the multiplier is large.

## What it needs

- The layout's page/line accounting available to the inliner, which means
  inlining and layout stop being independent phases
  ([O0402](O0402-layout-aware-outlining.md) is the same coupling seen from the
  other end).
- Profile weights ([O0268](O0268-profile-collection.md)) to know which call
  sites are worth the growth.
