# O0337 — Horner / Estrin polynomial evaluation

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0061](O0061-reassociation.md), [O0121](O0121-reduction-tree-balancing.md), [O0343](O0343-transcendental-specialization.md) |

## The idea

`a*x^3 + b*x^2 + c*x + d` evaluated literally costs three powers and three
multiplies. **Horner's** form — `((a*x + b)*x + c)*x + d` — costs three multiplies
and three adds in a compact serial chain, and **Estrin's** form splits it into
independent sub-expressions that a superscalar target can overlap.

The choice between them is a target question: Horner for a machine with one ALU
and no pipelining (an 8086), Estrin where instruction-level parallelism exists.

## Applies to

```basic
DIM x!, y!
y! = a! * x! ^ 3 + b! * x! ^ 2 + c! * x! + d!
```

## What it needs

- Recognition of a polynomial in one variable, including the `^` forms — which
  on the float path are `rt_pow` calls, so the rewrite also removes those.
- **Float rewriting changes rounding**: Horner's form is not bit-identical to
  the naive evaluation, so this belongs behind an explicit fast-math mode
  ([O0344](O0344-fp-reassociation.md)) unless the operands are exactly
  representable.
- Integer polynomials have no such restriction — reassociation is exact modulo
  2ⁿ ([O0061](O0061-reassociation.md)).
