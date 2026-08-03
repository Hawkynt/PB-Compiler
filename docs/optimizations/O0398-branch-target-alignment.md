# O0398 — Branch target alignment

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0231](O0231-loop-top-alignment.md), [O0378](O0378-cache-line-block-placement.md), [O0397](O0397-indirect-target-clustering.md) |

## The idea

Loop tops are aligned today ([O0231](O0231-loop-top-alignment.md)); other
**heavily taken branch destinations** are not — a hot `SELECT` arm, a common
indirect target, the merge point of a hot diamond. Aligning those according to
the target's fetch rules gives the same benefit for the same reason.

## What it needs

- Target-taken counts ([O0268](O0268-profile-collection.md)), so only the
  destinations that matter are padded
  ([O0379](O0379-selective-loop-alignment.md) makes the same argument for
  loops).
- The fetch rules per target ([O0174](O0174-target-cost-models.md)) — alignment
  granularity differs, and on an 8086 there is none worth having.
- The padding must be **unreachable by fall-through**, or it executes; for a
  branch destination that means the pad goes before the label, after an
  unconditional transfer.
