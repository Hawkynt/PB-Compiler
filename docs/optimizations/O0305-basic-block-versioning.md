# O0305 — Basic-block versioning

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0304](O0304-guarded-specialization.md), [O0156](O0156-path-sensitive-propagation.md), [O0107](O0107-branch-folding-through-phi.md) |

## The idea

Create specialized copies of a CFG **region** for different fact sets — a range,
an alignment, a known value — and route execution into the right one. Where
path-sensitive propagation ([O0156](O0156-path-sensitive-propagation.md)) keeps
the facts *in the analysis*, versioning materializes them **in the code**, so
every downstream pass sees a region where the fact simply holds.

## Applies to

```basic
DIM x%, i%
' after this test, one version knows x% is small and the other does not
IF x% < 256 THEN
  FOR i% = 0 TO 99 : a%(i% + x%) = 0 : NEXT
ELSE
  FOR i% = 0 TO 99 : a%(i% + x%) = 0 : NEXT
END IF
```

## What it needs

- A budget and a **profitability rule**: a version only pays if the fact enables
  something concrete (a dropped check, a vectorization, a narrower type).
- Correct phi/merge handling where the versions rejoin, and the guarantee that
  the unspecialized version remains correct on its own
  ([O0304](O0304-guarded-specialization.md)).
