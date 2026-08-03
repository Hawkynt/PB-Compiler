# O0152 — Runtime dependence-check versioning

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0026](O0026-auto-vectorization.md), [O0171](O0171-alias-analysis.md), [O0130](O0130-trip-count-versioning.md) |

## The idea

Vectorizing `c(i) = a(i) + b(i)` is only legal if `c` does not overlap `a` or
`b` in a way that makes the vector order differ from the element order. When the
compiler cannot prove non-overlap statically — dynamic arrays, arrays reached
through pointers, `BYREF` parameters — it can **test at run time**: compare the
pointer ranges, run the vector path if they are disjoint and the scalar path
otherwise.

## Applies to

```basic
SUB AddArrays(a%(), b%(), c%(), BYVAL n%)
  DIM i%
  FOR i% = 0 TO n%
    c%(i%) = a%(i%) + b%(i%)      ' the caller may have passed the same array twice
  NEXT
END SUB
```

## Today

Not vectorized: the arrays arrive `BYREF` and nothing proves they are distinct.

## Planned

```asm
    ; if (c+len <= a or a+len <= c) and (c+len <= b or b+len <= c)
    ...
    jbe     VectorPath
    jmp     ScalarPath
```

## What it needs

- The **range test** itself (base + length comparisons), which is cheap — the
  cost is the duplicated loop body, so a code-size budget applies.
- Static alias facts first ([O0171](O0171-alias-analysis.md)): most arrays *are*
  provably distinct, and the runtime check should only be emitted for the ones
  that are not.
- The same versioning machinery [O0130](O0130-trip-count-versioning.md) needs,
  so the two should share one implementation.
