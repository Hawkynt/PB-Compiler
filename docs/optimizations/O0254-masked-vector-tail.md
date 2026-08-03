# O0254 — Masked vector tail

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0146](O0146-vector-tail.md), [O0026](O0026-auto-vectorization.md), [O0150](O0150-vector-compare-select.md) |
| **Split from** | [O0146](O0146-vector-tail.md) |

## The idea

Instead of a scalar remainder loop, run the tail **in the vector unit** with the
out-of-range lanes masked off: AVX-512 has native masked load/store, and SSE can
emulate it with a compare-generated mask plus a blend.

One code path for the whole loop, and no branch to a separate tail.

## Applies to

```basic
$CPU 80586 AVX512
$OPTIMIZE SPEED
FOR i% = 0 TO n%             ' n% not a multiple of the lane count
  c%(i%) = a%(i%) + b%(i%)
NEXT
```

## What it needs

- Native masking (AVX-512) or a mask-and-blend emulation whose **store** does
  not write the suppressed lanes — a read-modify-write blend would clobber
  memory past the array.
- The mask itself, computed from the remaining count: a lane index vector
  compared against the limit.
