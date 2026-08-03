# O0148 — Packed narrow vs widening vector arithmetic

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0026](O0026-auto-vectorization.md), [O0016](O0016-value-fact-analysis.md), [O0149](O0149-saturating-pack.md) |

## The idea

A byte-element loop can run at **16 lanes per 128-bit vector** if the arithmetic
stays in bytes, or at 8 lanes if each byte must be widened to a word first. The
choice is a value-range question: if the lattice proves no lane can overflow its
narrow width, the packed-narrow form is twice as fast; if it cannot, the values
must be unpacked, computed and repacked.

This is [O0016](O0016-value-fact-analysis.md)'s known-bits and interval domains
applied per lane — the same proof that already lets a 32-bit multiply run on the
16-bit ALU.

## Applies to

```basic
$CPU 80586 SSE
$OPTIMIZE SPEED
DIM i%, a(0 TO 999) AS BYTE, b(0 TO 999) AS BYTE, c(0 TO 999) AS BYTE
FOR i% = 0 TO 999
  c(i%) = (a(i%) \ 2) + (b(i%) \ 2)     ' each term <= 127: no byte overflow
NEXT
```

## Today

Byte-element loops are not vectorized at all (the recognizer requires 2-byte
elements).

## Planned

Because both halves are provably ≤ 127, the sum fits a byte and the loop runs
`PADDB` at 16 lanes — instead of unpacking to words, adding, and packing back.

## What it needs

- Per-lane range facts, which are the *element* facts the array's declared type
  and the arithmetic already provide.
- The widening path as the fallback (`PUNPCKLBW`/`PUNPCKHBW`, word arithmetic,
  `PACKUSWB`), so the transform is a choice between two correct lowerings rather
  than a gamble.
- Wrap semantics per lane must match the scalar `BYTE` arithmetic exactly, which
  is the invariant [O0026](O0026-auto-vectorization.md) already holds itself to.
