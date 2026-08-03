# O0333 — Lookup-table elimination

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0332](O0332-lookup-table-generation.md), [O0174](O0174-target-cost-models.md), [O0004](O0004-strength-reduction.md) |

## The idea

The reverse trade. A table lookup was the right answer in 1990, when a multiply
cost 120 cycles and memory was fast relative to the CPU. On a modern host the
ratio is inverted: a cache miss costs hundreds of cycles and the arithmetic is
free.

Where a table's contents are a **simple function of the index**, recomputing can
beat loading — and the table itself disappears from the image.

## Applies to

```basic
DIM sq%(0 TO 255), i%
FOR i% = 0 TO 255 : sq%(i%) = i% * i% : NEXT     ' a table of squares
...
PRINT sq%(n%)                                     ' -> PRINT n% * n%
```

## What it needs

- Recognition that the table is a **pure function of its index**, provable by
  running [O0332](O0332-lookup-table-generation.md)'s machinery backwards over
  the initializing loop.
- The decision is entirely a **cost-model** question
  ([O0174](O0174-target-cost-models.md)) and the answer differs by target: on an
  8086 with no cache the table usually wins; on the C back end targeting a modern
  host it usually loses.
- The table must not be written elsewhere, and must not escape.
