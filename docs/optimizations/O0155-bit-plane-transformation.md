# O0155 — Bit-plane and bit-sliced transformation

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end + emitter |
| **Related** | [O0153](O0153-swar-arithmetic.md), [R0002](R0002-fast-graphics.md), [O0073](O0073-algorithmic-idiom-catalog.md) |

## The idea

When the elements are **Boolean** — a mask, a monochrome bitmap, a cellular
automaton state, a set membership array — one bit per element is the natural
representation, and every operation over them becomes a single wide bitwise
instruction over 16 or 32 elements at once.

The related **bit-slicing** transform goes further: represent N values by their
bit planes, so an operation on all N runs as a small circuit of bitwise
operations. It is the standard technique for cryptographic inner loops and for
cellular/graphics kernels — and it needs no SIMD hardware at all.

## Applies to

```basic
DIM i%, alive%(0 TO 999), next%(0 TO 999)
FOR i% = 1 TO 998
  next%(i%) = (alive%(i%-1) XOR alive%(i%+1))     ' rule 90, one cell per iteration
NEXT
```

## Today

One INTEGER per cell, one iteration per cell — 16 bits of storage and a full
loop iteration to compute one bit of information.

## Planned

```asm
    mov     ax, [si]         ; 16 cells packed as bits
    mov     bx, ax
    shl     ax, 1
    shr     bx, 1
    xor     ax, bx           ; 16 cells updated in one instruction
    mov     [di], ax
```

## What it needs

- A **representation change**, which is far more than a peephole: the array's
  storage, every read and write, and the boundary conversions all change. That
  makes it a whole-array analysis (all elements provably 0/−1, no address taken,
  no external exposure) rather than a local rewrite.
- The neighbour-shifting at word boundaries has to be exact, which is where a
  hand-written implementation usually gets it wrong.
- It overlaps [R0002](R0002-fast-graphics.md)'s planar EGA/VGA work, where the
  hardware layout is *already* bit-planed.
