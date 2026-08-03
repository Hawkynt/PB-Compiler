# O0280 — Argument structure reduction

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Whole-program |
| **Related** | [O0069](O0069-dead-parameter-elimination.md), [O0059](O0059-scalar-replacement.md), [O0282](O0282-internal-calling-convention.md) |

## The idea

A procedure that takes a whole `TYPE` (or a descriptor) but reads only two of
its fields does not need the aggregate. Passing the fields instead removes the
copy or the indirection at every call site — and turns a BYREF aggregate
parameter into `BYVAL` scalars, which then travel in registers
([O0021](O0021-register-parameters.md)).

## Applies to

```basic
TYPE Rect
  x AS INTEGER
  y AS INTEGER
  w AS INTEGER
  h AS INTEGER
END TYPE

FUNCTION Area%(r AS Rect)    ' reads only w and h
  Area% = r.w * r.h
END FUNCTION
```

becomes `FUNCTION Area%(BYVAL w%, BYVAL h%)`.

## What it needs

- Per-field use analysis inside the callee (the same census
  [O0163](O0163-dead-field-elimination.md) needs) plus the ownership proof that
  the compiler sees every call site.
- The callee must not **write** through the reference, or the fields must be
  passed back — which is where [O0281](O0281-return-structure-reduction.md)
  comes in.
