# O0403 — Scenario-weighted layout

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0373](O0373-phase-aware-layout.md), [O0268](O0268-profile-collection.md), [O0406](O0406-layout-assertion-battery.md) |

## The idea

Optimizing for **one** profiling run produces a layout that is excellent for
that run and arbitrary for everything else. Combining several representative
profiles with explicit weights — an interactive session, a batch conversion, a
short invocation — produces a layout that is good across the workloads the
program actually has.

## What it needs

- A profile format that can carry several named scenarios with weights
  ([O0268](O0268-profile-collection.md)).
- A combined objective: the placement search minimizes the **weighted sum** of
  per-scenario working sets ([O0375](O0375-working-set-minimization.md)) rather
  than one of them.
- The honesty to report per-scenario results separately as well, since a
  weighted average can hide a scenario that got much worse
  ([O0406](O0406-layout-assertion-battery.md)).
