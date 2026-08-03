# O0267 — Modulo scheduling

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Scheduler |
| **Related** | [O0128](O0128-software-pipelining.md), [O0175](O0175-critical-path-scheduling.md), [O0174](O0174-target-cost-models.md) |
| **Split from** | [O0128](O0128-software-pipelining.md) |

## The idea

The general form of software pipelining: choose an **initiation interval** II —
one new logical iteration started every II cycles — and schedule the loop body so
that no resource is oversubscribed *modulo* II. The kernel then executes
overlapping stages of several iterations at a steady rate.

Where [O0128](O0128-software-pipelining.md) is the shape (prologue, kernel,
epilogue), this is the algorithm that decides what goes where.

## Applies to

Any loop whose body has enough independent work to overlap — typically an
elementwise array computation with a multiply or a memory latency to hide.

## What it needs

- A **resource model**: units, issue widths, latencies — per target
  ([O0174](O0174-target-cost-models.md)). The minimum II is bounded from below
  by both the resource usage and the loop-carried dependency chain, so the model
  is not optional.
- Registers for the values in flight across stages
  ([O0058](O0058-386-register-allocation.md)), and modulo variable expansion
  where a value's lifetime exceeds II.
