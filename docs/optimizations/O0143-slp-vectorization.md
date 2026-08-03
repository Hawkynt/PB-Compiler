# O0143 — Superword-level parallelism (SLP)

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0026](O0026-auto-vectorization.md), [O0074](O0074-wider-vectorization.md), [O0007](O0007-loop-unrolling.md) |

## The idea

[O0026](O0026-auto-vectorization.md) vectorizes **loops**. SLP vectorizes
**straight-line code**: isomorphic independent scalar statements in one basic
block are packed into a vector operation, whether or not they came from a loop.

Unrolling first and running SLP afterwards ("unroll-then-SLP") is the standard
way to catch loops the loop vectorizer rejects — the unroller produces the
adjacent isomorphic statements that SLP then packs.

## Applies to

```basic
TYPE Vec4
  x AS INTEGER
  y AS INTEGER
  z AS INTEGER
  w AS INTEGER
END TYPE
DIM a AS Vec4, b AS Vec4, c AS Vec4
c.x = a.x + b.x
c.y = a.y + b.y
c.z = a.z + b.z
c.w = a.w + b.w
```

## Today

Four scalar loads, four adds, four stores.

## Planned

```asm
    movq    mm0, [a]         ; four 16-bit lanes
    paddw   mm0, [b]
    movq    [c], mm0
    emms
```

## What it needs

- A **packing heuristic**: find groups of isomorphic operations over adjacent
  memory, build the vector tree bottom-up, and cost it against the scalar form
  ([O0174](O0174-target-cost-models.md)) — packing that needs shuffles to
  assemble its operands usually loses.
- The same wrap-per-lane correctness argument
  [O0026](O0026-auto-vectorization.md) already establishes.
- Field adjacency facts, which the `TYPE` layout provides exactly
  ([O0015](O0015-udt-zero-cost.md)).
