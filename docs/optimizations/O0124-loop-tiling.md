# O0124 — Loop tiling (blocking)

| | |
|---|---|
| **Status** | ⬜ Planned (no effect on a cacheless 8086; relevant from the 486 onward) |
| **Stage** | Mid-end |
| **Related** | [O0122](O0122-loop-interchange.md), [O0172](O0172-loop-dependence-analysis.md), [O0174](O0174-target-cost-models.md) |

## The idea

Process a multidimensional array in blocks sized to the cache instead of in full
rows or columns, so a value loaded once is reused before it is evicted. The
classic case is a matrix operation where the natural loop order touches each
element of one operand a whole row apart.

## Applies to

```basic
DIM a%(0 TO 255, 0 TO 255), b%(0 TO 255, 0 TO 255), i%, j%
FOR i% = 0 TO 255
  FOR j% = 0 TO 255
    b%(j%, i%) = a%(i%, j%)      ' a transpose: every access misses
  NEXT
NEXT
```

## Planned

```basic
FOR ii% = 0 TO 255 STEP 16
  FOR jj% = 0 TO 255 STEP 16
    FOR i% = ii% TO ii% + 15
      FOR j% = jj% TO jj% + 15
        b%(j%, i%) = a%(i%, j%)  ' a 16x16 tile stays resident
      NEXT
    NEXT
  NEXT
NEXT
```

## What it needs

- **A cache to tile for.** The 8086 and 286 have none; the 486 has 8 KB unified,
  the Pentium 8 KB + 8 KB. So the tile size is a per-target parameter and the
  transform must be off entirely for the earlier targets
  ([O0174](O0174-target-cost-models.md)).
- Dependence analysis ([O0172](O0172-loop-dependence-analysis.md)) to prove the
  reordering legal, and the same post-loop counter-value obligation every loop
  transform carries.
- Realistically this is the least valuable item in the loop family for a DOS
  target — listed for completeness, not for priority.
