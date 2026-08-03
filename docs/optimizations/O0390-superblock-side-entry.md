# O0390 — Superblock formation by side-entry duplication

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Layout + mid-end |
| **Related** | [O0106](O0106-trace-formation.md), [O0389](O0389-hot-trace-layout.md), [O0392](O0392-hot-code-duplication.md) |

## The idea

A trace with **side entries** — blocks reachable from outside it — cannot be
treated as a single unit: an optimization valid along the trace may be invalid
for a path that jumps into the middle. Duplicating the entered blocks gives the
trace a single entry (a *superblock*), after which scheduling, register
allocation and value propagation may treat it as straight-line code.

## What it needs

- A duplication budget: this trades code size for optimization scope, and it is
  the classic way to make a hot path fast at the cost of a colder copy.
- Correct CFG maintenance — every duplicated block's successors and phi inputs
  belong to its own copy ([O0107](O0107-branch-folding-through-phi.md) has the
  same obligation).
- It is the enabling transform for
  [O0389](O0389-hot-trace-layout.md) whenever the hot path is entered from more
  than one place, which in loop-heavy code is most of the time.
