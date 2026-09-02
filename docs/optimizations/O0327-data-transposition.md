# O0327 — Data transposition

| | |
|---|---|
| **Status** | ✅ Implemented for private zero-based affine 2D scalar arrays |
| **Stage** | Whole-program data layout |
| **IR** | ✅ `Ir/Passes/DataLayoutTransforms.cs` — recovers row/column coefficients and transposes storage when a counted innermost loop walks the strided dimension; opaque/escaping layouts decline |
| **Related** | [O0320](O0320-aos-to-soa.md), [O0122](O0122-loop-interchange.md), [O0144](O0144-interleaved-access-vectorization.md) |

## The idea

Store multidimensional data in the order the program **traverses** it, so the
innermost loop walks contiguous memory. Where
[O0122](O0122-loop-interchange.md) changes the loop to fit the layout, this
changes the layout to fit the loop — the right choice when the loop order is
fixed by the algorithm or by observable output order.

## Applies to

```basic
DIM img%(0 TO 199, 0 TO 319), y%, x%
FOR y% = 0 TO 199
  FOR x% = 0 TO 319
    img%(y%, x%) = 0         ' the natural raster order, against the storage order
  NEXT
NEXT
```

## What it needs

- A whole-program traversal census: if two loops disagree about the preferred
  order, transposing helps one and hurts the other.
- The layout must not be observable — no `VARPTR` arithmetic over the array, no
  `BSAVE`/`BLOAD` of its storage, no `FIELD`/file record dependence, no external
  unit sharing ([O0260](O0260-escape-analysis.md)).
- For SCREEN 13 pixel arrays the "layout" is fixed by the hardware
  ([R0002](R0002-fast-graphics.md)) and the transform is illegal by definition —
  a useful reminder that some arrays are memory-mapped contracts, not data.

The current transform intentionally restricts itself to zero-based, affine 2D
private arrays and exact counted loops. Non-zero lower bounds and competing
traversal orders remain outside this first implementation rather than being
silently remapped from incomplete information.
