# O0326 — Cache-conflict padding

| | |
|---|---|
| **Status** | ✅ Implemented for private affine 2D scalar arrays when cache geometry is supplied |
| **Stage** | Data layout |
| **IR** | ✅ `Ir/Passes/DataLayoutTransforms.cs` — detects row strides that repeat the same cache set, pads each physical row by one cache line, and rewrites affine element addresses; disabled when the target has no cache model |
| **Related** | [O0325](O0325-array-padding-alignment.md), [O0124](O0124-loop-tiling.md), [O0174](O0174-target-cost-models.md) |

## The idea

For a direct-mapped cache, a stride equal to the cache size maps successive
references onto the same set. For an N-way set-associative cache the period is
smaller: Intel documents the conflicting stride as the cache size divided by the
number of ways. Treating every cache as direct-mapped therefore misses exactly
the conflicts the extra ways merely postpone.

O0326 compares the logical row stride against that set-conflict period. When it
matches, the physical row receives at least one cache line of padding (rounded
up to whole elements). The next row therefore starts in a different set instead
of merely moving by one scalar element inside the same line.

## Applies to

```basic
DIM a(0 TO 255, 0 TO 255) AS SINGLE
DIM b(0 TO 255, 0 TO 255) AS SINGLE
FOR i% = 0 TO 255
  s! = s! + a(i%, k%) * b(i%, k%)
NEXT
```

If the physical row stride is a multiple of the target cache's set-conflict
period, the column walk repeatedly selects the same set. Padding changes only
the private physical layout; the logical subscripts remain unchanged.

## What it needs

- Cache geometry from the target model
  ([O0174](O0174-target-cost-models.md)): size, line size, and associativity. On
  a target without a known cache model the pass must not guess; on an 8086 or
  286 there is no on-chip cache to optimize in the first place.
- The same non-observability rules as
  [O0325](O0325-array-padding-alignment.md): `UBOUND`, `ERASE`, file records and
  `VARPTR` arithmetic must not see the pad.
- A completely rebuildable affine address set. All rewritten offsets are
  preflighted before storage is replaced, so a declined access cannot leave a
  partially transformed function.

The IR pass therefore has no default cache constants. A caller supplies cache
size, line size, and associativity through `IrDataLayoutTarget`; otherwise it is
not inserted into the standard pipeline. `CacheAssociativity` defaults to one
for callers that intentionally model a direct-mapped cache.
