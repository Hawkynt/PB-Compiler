# O0172 — Loop dependence analysis

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Analysis infrastructure |
| **Related** | [O0171](O0171-alias-analysis.md), [O0122](O0122-loop-interchange.md), [O0123](O0123-loop-distribution.md), [O0026](O0026-auto-vectorization.md) |

## The idea

Whether two array accesses from **different iterations** can touch the same
element — and if so, in which direction — is the fact that decides whether a
loop may be vectorized, interchanged, distributed, fused, tiled or skewed. It is
the single analysis that unblocks the whole loop family.

The classical machinery: express each subscript as an affine function of the
loop counters, then test the resulting equations (GCD test, Banerjee test, exact
tests for the common shapes) for solutions inside the iteration space, and
record the dependence **direction vectors**.

## Applies to

```basic
DIM i%, a%(0 TO 999)
FOR i% = 1 TO 999
  a%(i%) = a%(i% - 1) + 1     ' a true dependence: NOT vectorizable
NEXT

FOR i% = 0 TO 998
  a%(i%) = a%(i% + 1) + 1     ' an anti-dependence: vectorizable with care
NEXT
```

## Today

The vectorizer requires three **distinct arrays** (`c(i) = a(i) OP b(i)`) — a
syntactic proxy for "no dependence" that is correct but rejects every loop
touching one array twice, including both examples above.

## Planned

The first loop is rejected on its dependence, the second is vectorized, and the
loop-restructuring transforms ([O0122](O0122-loop-interchange.md),
[O0123](O0123-loop-distribution.md), [O0062](O0062-loop-restructuring.md))
become possible at all.

## What it needs

- Affine subscript recognition, which needs the induction-variable analysis
  ([O0110](O0110-general-induction-variables.md)).
- The alias oracle ([O0171](O0171-alias-analysis.md)) for the base objects — a
  dependence test on two arrays that might be the same array is meaningless.
- **Wrap-aware index reasoning**: a 16-bit subscript expression that can wrap
  breaks the affine model, the same trap
  [O0016](O0016-value-fact-analysis.md) documents for composed ranges.
