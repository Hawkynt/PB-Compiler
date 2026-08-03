# O0402 — Layout-aware outlining

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Whole-program |
| **Related** | [O0275](O0275-cold-code-outlining.md), [O0400](O0400-page-boundary-outlining.md), [O0401](O0401-layout-aware-inlining.md) |

## The idea

Outline code **specifically so that** the remaining hot region fits into one
cache line, one page, or one segment. The goal is not the outlined fragment's
own cost but the *fit* of what is left behind — the inverse of
[O0401](O0401-layout-aware-inlining.md)'s question.

Together they make one decision with two directions: move code out of the hot
region, or pull it in, according to what makes the hot region fit.

## What it needs

- A **fit target** — the line, page or segment size from the target model
  ([O0174](O0174-target-cost-models.md)) — and the current size of the hot
  region, which only the layout stage knows.
- The extraction mechanics of [O0275](O0275-cold-code-outlining.md): live values
  at the boundary, and the frame the outlined code runs on.
- An iteration between outlining and placement, since each changes the other's
  input.
