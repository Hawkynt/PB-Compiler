# O0351 — Pointer and handle check elimination

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0097](O0097-repeated-comparison-elimination.md), [O0045](O0045-ir-correlated-value-propagation.md), [O0181](O0181-empty-string-comparison.md) |

## The idea

PB has no null-pointer *fault*, but it has the same shape of redundant test:
a pointer or string handle checked for zero, dereferenced, then checked again —
and array-descriptor validity tests before every access to a dynamic array.

A check **dominated** by a successful check or by a dereference is redundant.

## Applies to

```basic
DIM p AS INTEGER POINTER
IF p <> 0 THEN
  PRINT @p
  IF p <> 0 THEN PRINT "still valid"     ' provably true
END IF
```

## What it needs

- The dominator-scoped fact propagation of
  [O0097](O0097-repeated-comparison-elimination.md) — the same machinery, a
  different predicate.
- A rule about what a **dereference implies**: on a machine with no memory
  protection, dereferencing a null pointer does not fault, it reads segment
  zero — so "it was dereferenced, therefore it was non-null" is *not* sound
  here. Only an explicit preceding test counts.
- For string handles, the representation invariant of
  [O0181](O0181-empty-string-comparison.md).
