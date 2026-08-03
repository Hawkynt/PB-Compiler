# O0338 — Reciprocal reuse across repeated divisions

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0028](O0028-loop-invariant-code-motion.md), [O0341](O0341-reciprocal-approximation.md), [O0345](O0345-common-denominator-factoring.md) |

## The idea

Dividing repeatedly by the **same loop-invariant** value computes the reciprocal
once and multiplies thereafter: on the x87, `FDIV` is several times slower than
`FMUL`, so a loop of divisions becomes a loop of multiplications plus one
division.

## Applies to

```basic
DIM i%, d!, a!(0 TO 999)
FOR i% = 0 TO 999
  a!(i%) = a!(i%) / d!       ' d! never changes
NEXT
```

becomes

```basic
r! = 1! / d!
FOR i% = 0 TO 999 : a!(i%) = a!(i%) * r! : NEXT
```

## What it needs

- **It is not bit-exact.** `x / d` and `x * (1/d)` differ in the last bit for
  most `d`, so this belongs behind an explicit fast-math mode — except when `d`
  is a power of two, where the reciprocal is exact and the rewrite is always
  legal.
- Loop-invariance of the divisor ([O0028](O0028-loop-invariant-code-motion.md)),
  and a zero-divisor guard in the preheader that fires exactly where the first
  division would have.
