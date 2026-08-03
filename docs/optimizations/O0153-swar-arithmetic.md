# O0153 — SWAR packed arithmetic

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0026](O0026-auto-vectorization.md), [O0137](O0137-load-widening.md), [O0154](O0154-swar-search.md) |

## The idea

**SIMD Within A Register**: use ordinary integer registers as packed lanes when
no SIMD unit exists. A 16-bit register holds two bytes; a 32-bit register (386+)
holds four. Addition works lane-wise as long as carries cannot cross a lane
boundary — which is arranged by masking, or guaranteed by a range proof.

This is the technique that brings vectorization to the **8086 and 286**, where
MMX is decades away. It is not exotic: masking off the high bit of each lane,
adding, and correcting is a well-known trick, and for `AND`/`OR`/`XOR` there is
no correction at all — bitwise operations are already perfectly lane-parallel.

## Applies to

```basic
DIM i%, a(0 TO 999) AS BYTE, b(0 TO 999) AS BYTE, c(0 TO 999) AS BYTE
FOR i% = 0 TO 999
  c(i%) = a(i%) XOR b(i%)        ' bitwise: no carries at all
NEXT
```

## Today

One byte load, one `XOR`, one byte store per element.

## Planned

```asm
    mov     ax, [si]         ; two bytes
    xor     ax, [di]         ; both lanes at once, no correction needed
    mov     [bx], ax
```

Two elements per iteration on an 8086; four under `$CPU 80386`.

## What it needs

- The **lane-isolation proof**: for `AND`/`OR`/`XOR`/`NOT` it is free; for
  `+`/`-` the carry must be shown not to cross, which needs either a mask-and-
  correct sequence or the range facts from
  [O0016](O0016-value-fact-analysis.md).
- Alignment and over-read safety for the wide access
  ([O0139](O0139-alignment-versioning.md)), and a tail for the odd element.
- Byte-order care: the low lane is the low address, so the packed result must be
  stored back in the same orientation.
