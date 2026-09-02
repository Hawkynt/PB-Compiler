# O0326 — Cache-conflict padding

| | |
|---|---|
| **Status** | ✅ Implemented for private affine 2D scalar arrays when cache geometry is supplied |
| **Stage** | Data layout |
| **IR** | ✅ `Ir/Passes/DataLayoutTransforms.cs` — detects row strides that are exact cache-size multiples, adds physical row padding, and rewrites affine element addresses; disabled when the target has no cache model |
| **Related** | [O0325](O0325-array-padding-alignment.md), [O0124](O0124-loop-tiling.md), [O0174](O0174-target-cost-models.md) |

## The idea

Arrays whose stride is an exact multiple of the cache size map onto the same
cache **sets**, so a loop touching several of them evicts each on every
iteration — the classic power-of-two stride pathology. Adding a small pad to the
row length breaks the alignment and the conflicts disappear.

## Applies to

```basic
DIM a(0 TO 255, 0 TO 255) AS SINGLE     ' 1 KB rows: every row maps to one set
DIM b(0 TO 255, 0 TO 255) AS SINGLE
FOR i% = 0 TO 255
  s! = s! + a(i%, k%) * b(i%, k%)       ' both columns thrash the same sets
NEXT
```

## What it needs

- Cache geometry from the target model
  ([O0174](O0174-target-cost-models.md)) — and the recognition that on an 8086
  or 286 there is **no cache at all**, so the padding would be pure waste.
- The same non-observability rules as
  [O0325](O0325-array-padding-alignment.md): `UBOUND`, `ERASE`, file records and
  `VARPTR` arithmetic must not see the pad.

The IR pass therefore has no default cache constants. A caller must supply the
cache size (and optionally line size) through `IrDataLayoutTarget`; otherwise it
is not inserted into the standard pipeline.
