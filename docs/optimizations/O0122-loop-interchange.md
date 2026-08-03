# O0122 — Loop interchange

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0062](O0062-loop-restructuring.md), [O0110](O0110-general-induction-variables.md), [O0172](O0172-loop-dependence-analysis.md) |

## The idea

Swapping the nesting order of two loops changes a strided traversal into a
contiguous one. PowerBASIC arrays are **column-major** in the classic layout, so
`a(i, j)` with `j` innermost strides by the row length, while `i` innermost
walks adjacent elements — a difference between a pointer increment and a
multiply-and-add per access.

## Applies to

```basic
DIM a%(0 TO 99, 0 TO 99), i%, j%, s%
FOR i% = 0 TO 99
  FOR j% = 0 TO 99
    s% = s% + a%(i%, j%)     ' strided
  NEXT
NEXT
```

## Today

Each access recomputes the flattened index; the walk jumps by a row each step,
so no pointer stepping applies.

## Planned

```basic
FOR j% = 0 TO 99
  FOR i% = 0 TO 99
    s% = s% + a%(i%, j%)     ' contiguous: the inner loop steps by one element
  NEXT
NEXT
```

which then qualifies for [O0030](O0030-induction-variable-strength-reduction.md)
pointer stepping and, on a wide target, for vectorization.

## What it needs

- **Dependence analysis** ([O0172](O0172-loop-dependence-analysis.md)):
  interchange is legal only when no dependence direction vector forbids it.
- Both counters' **post-loop values** must be preserved — PB leaves them live,
  so the interchange must reproduce exactly the same final values.
- Rectangular bounds (the inner limit must not depend on the outer counter), or
  the interchange changes the iteration set.
