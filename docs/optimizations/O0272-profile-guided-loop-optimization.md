# O0272 — Profile-guided loop optimization

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end policy |
| **Related** | [O0129](O0129-unroll-factor-cost-model.md), [O0130](O0130-trip-count-versioning.md), [O0268](O0268-profile-collection.md) |

## The idea

Unroll factors, vector widths, peeling decisions and loop versioning are all
guesses without trip-count data. A **distribution** — not just an average —
answers them properly:

| Observed trips | Right answer |
|---|---|
| almost always 0 or 1 | do not unroll; consider peeling the guard |
| a small constant | unroll fully ([O0007](O0007-loop-unrolling.md)) |
| large and variable | vectorize with a runtime tail |
| bimodal | version the loop ([O0130](O0130-trip-count-versioning.md)) |

## Applies to

```basic
FOR i% = 0 TO n%             ' n% is 3 in 90% of runs and 30 000 in the rest
  ...
NEXT
```

— exactly the case where one static choice is wrong for one of the two
populations.

## What it needs

- Trip-count histograms per loop ([O0268](O0268-profile-collection.md)).
- The cost model to consume them ([O0129](O0129-unroll-factor-cost-model.md)),
  and the versioning machinery to act on a bimodal distribution.
