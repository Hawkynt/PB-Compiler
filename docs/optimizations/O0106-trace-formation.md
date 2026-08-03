# O0106 — Trace formation and superblock tail duplication

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end + layout |
| **Related** | [O0104](O0104-block-placement.md), [O0105](O0105-hot-cold-splitting.md), [O0038](O0038-instruction-scheduling.md) |

## The idea

Group the blocks along a likely execution path into a **trace**, then optimize
and schedule across the whole trace instead of block by block. Where a small
join block interrupts a trace, **duplicate** it into each predecessor
(tail duplication), producing a *superblock* with a single entry — which gives
the scheduler and the value analyses a much longer straight-line run to work
with.

This is the transformation that makes [O0038](O0038-instruction-scheduling.md)'s
windows big enough to matter: today every label ends a window.

## Applies to

```basic
DIM i%, a%(0 TO 99), s%
FOR i% = 0 TO 99
  IF a%(i%) > 0 THEN s% = s% + a%(i%) ELSE s% = s% - a%(i%)
  s% = s% AND 32767                    ' the join block
NEXT
```

## Today

The join (`s% = s% AND 32767`) ends both arms' scheduling windows and clears the
CSE cache at the merge unless `RetainPastMerge` applies.

## Planned

The join is duplicated into both arms, so each arm becomes one straight-line
trace that schedules and CSEs as a unit.

## What it needs

- Edge probabilities ([O0104](O0104-block-placement.md)) to pick the trace.
- A **code-size budget**: duplication grows the image, so it needs the cost
  model ([O0174](O0174-target-cost-models.md)) and should be off under
  `$OPTIMIZE SIZE`.
- Correct phi/merge handling if it runs on the SSA form — each duplicate takes
  the incoming values of its own predecessor.
