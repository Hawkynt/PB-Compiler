# O0345 — Common-denominator factoring

| | |
|---|---|
| **Status** | ⬜ Planned — fast-math mode only |
| **Stage** | Mid-end |
| **Related** | [O0338](O0338-reciprocal-sequence-reuse.md), [O0003](O0003-common-subexpression-elimination.md), [O0344](O0344-fp-reassociation.md) |

## The idea

Several divisions by the **same expression** become one division and several
multiplications:

```
a/d + b/d + c/d   ->   (a + b + c) / d
x/d, y/d          ->   r = 1/d : x*r, y*r
```

On the x87 an `FDIV` costs several times an `FMUL`, so trading two divisions for
one division and two multiplications is a clear win — where it is legal.

## Applies to

```basic
DIM x!, y!, z!, d!
x! = x! / d!
y! = y! / d!
z! = z! / d!
```

## What it needs

- **Fast-math gating**: `x/d` and `x*(1/d)` differ in the last bit, and summing
  before dividing changes the rounding of the intermediates. Both are
  observable.
- The divisor must be provably unchanged between the uses
  ([O0003](O0003-common-subexpression-elimination.md)'s invalidation rules) and
  non-zero, or the reciprocal's trap must fire where the first division's would
  have.
- The power-of-two case is exact and needs no gate at all.
