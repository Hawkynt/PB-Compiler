# O0146 — Vector tail strategies

| | |
|---|---|
| **Status** | ⬜ Planned (the fully-unrolled constant-remainder tail exists — [O0026](O0026-auto-vectorization.md)) |
| **Stage** | Emitter |
| **Related** | [O0026](O0026-auto-vectorization.md), [O0130](O0130-trip-count-versioning.md), [O0139](O0139-alignment-versioning.md) |
| **Split into** | [O0254](O0254-masked-vector-tail.md), [O0255](O0255-overlapping-vector-tail.md) |

## The idea

A vector loop processes `lanes` elements at a time; the last `n MOD lanes`
elements need separate treatment. Three strategies, in increasing order of
sophistication:

1. **Scalar tail** — what the current implementation does, fully unrolled
   because the remainder is a compile-time constant. Simple and always correct.
2. **Masked operations** — load and store with a lane mask, so the tail runs in
   the vector unit with the out-of-range lanes suppressed. Needs AVX-512-style
   masking (or a manual mask plus blend on SSE).
3. **Overlapping final vector** — process the last full vector's worth of
   elements *including some already-processed ones*, suppressing the duplicate
   stores. One extra vector iteration replaces the whole tail.

Strategy 3 is only safe where re-processing is idempotent and the overlap does
not re-read data a store has changed.

## Applies to

```basic
$CPU 80586 MMX
$OPTIMIZE SPEED
DIM i%, n%, a%(0 TO 999), b%(0 TO 999), c%(0 TO 999)
FOR i% = 0 TO n%            ' n% unknown: no constant remainder
  c%(i%) = a%(i%) + b%(i%)
NEXT
```

## Today

Not vectorized at all, because the tail cannot be unrolled without a constant
remainder.

## Planned

A runtime-computed scalar tail (strategy 1 generalized), which alone unblocks
variable-trip vectorization — and the masked/overlapping forms where the target
supports them.

## What it needs

- A runtime remainder loop, and the trip-count analysis
  ([O0131](O0131-exact-trip-count.md)) to compute where the vector part stops.
- For strategy 3, an idempotence proof for the overlapped elements.
- The correctness bar stays what it is today: byte-identical to the scalar loop
  for every trip count, including zero.
