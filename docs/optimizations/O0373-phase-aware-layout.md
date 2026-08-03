# O0373 — Phase-aware layout

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout |
| **Related** | [O0370](O0370-startup-code-clustering.md), [O0371](O0371-steady-state-clustering.md), [O0403](O0403-scenario-weighted-layout.md) |

## The idea

Programs have more than two phases. A DOS application typically runs through
**startup → loading → interactive use → batch processing → shutdown**, and each
phase has its own hot set. Generating a layout that keeps each phase's code
compact — rather than optimizing one global "hot" set — minimizes the working
set *per phase*, which is what the user actually experiences.

## What it needs

- A profile segmented by phase, which means either explicit markers or a
  clustering of the temporal profile
  ([O0362](O0362-temporal-function-clustering.md)).
- A placement objective that is a **sum over phases**, weighted by their
  duration or importance ([O0403](O0403-scenario-weighted-layout.md)) — a block
  used in two phases has to sit somewhere, and the weighting decides where.
