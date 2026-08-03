# O0314 — Task-graph extraction

| | |
|---|---|
| **Status** | ⬜ Planned — hosted back ends only (see [O0311](O0311-parallel-loop-versioning.md)) |
| **Stage** | Whole-program |
| **Related** | [O0161](O0161-function-summaries.md), [O0171](O0171-alias-analysis.md), [O0311](O0311-parallel-loop-versioning.md) |

## The idea

Independent calls or loop regions run concurrently. The compiler builds a task
graph from the **mod/ref summaries** ([O0161](O0161-function-summaries.md)):
two calls that touch disjoint memory and have no ordering effects can be issued
in parallel.

## Applies to

```basic
CALL ProcessLeft(data%())    ' touches only the left half
CALL ProcessRight(data%())   ' touches only the right half
```

## What it needs

- Precise mod/ref and alias information — the whole difficulty. Without it every
  pair of calls appears dependent, and with it the analysis is mostly done.
- **Ordering effects** count as dependencies: `PRINT`, file I/O, `TIMER`,
  `SOUND` and error handling all impose an order the program can observe, so the
  independence test is stricter than memory disjointness alone.
- A host runtime, and the same opt-in caution as the rest of this family.
