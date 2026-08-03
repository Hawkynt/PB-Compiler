# O0344 — Floating-point reassociation

| | |
|---|---|
| **Status** | ⬜ Planned — fast-math mode only |
| **Stage** | Mid-end |
| **Related** | [O0121](O0121-reduction-tree-balancing.md), [O0061](O0061-reassociation.md), [O0312](O0312-parallel-reduction.md) |

## The idea

Rebalancing a float reduction into a tree, or splitting it into several
accumulators, exposes the parallelism that
[O0120](O0120-multiple-accumulators.md) and
[O0145](O0145-vector-reduction.md) need — and which float chains otherwise
forbid entirely.

## Applies to

```basic
DIM i%, s!, a!(0 TO 999)
FOR i% = 0 TO 999
  s! = s! + a!(i%)           ' a serial float dependency chain
NEXT
```

## Why it is gated

Floating-point addition is **not associative**: `(a+b)+c` and `a+(b+c)` differ,
and the difference shows up in printed output. For the historic dialects that is
a fidelity failure, not a rounding tolerance — the whole project is built on
byte-identical output.

So it is available only under an explicit fast-math declaration, and never for a
dialect under oracle verification. The integer counterpart
([O0061](O0061-reassociation.md)) has no such restriction, because integer
arithmetic *is* associative modulo 2ⁿ.

## What it needs

- The FP mode model shared with [O0340](O0340-fma-contraction.md) and
  [O0341](O0341-reciprocal-approximation.md).
- A statement in the docs of exactly what a fast-math build no longer promises.
