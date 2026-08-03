# O0281 — Return structure reduction

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Whole-program |
| **Related** | [O0280](O0280-argument-structure-reduction.md), [O0102](O0102-return-value-forwarding.md), [O0282](O0282-internal-calling-convention.md) |

## The idea

A `FUNCTION` returning a `TYPE` by value (or a tuple —
`FUNCTION DivMod(...) AS (LONG, LONG)`) writes the whole aggregate through a
struct return. When callers only ever read one or two of its fields, the
unobserved fields need not be computed or stored, and the observed ones can come
back **in registers**.

## Applies to

```basic
TYPE Stats
  total AS LONG
  count AS LONG
  worst AS LONG
END TYPE

FUNCTION Analyze(a%()) AS Stats
  ...
END FUNCTION

DIM s AS Stats
s = Analyze(data%())
PRINT s.total                ' only .total is ever read
```

## What it needs

- A whole-program census of which returned fields any caller reads — the return
  counterpart of [O0280](O0280-argument-structure-reduction.md)'s parameter
  census.
- The dead field's computation may only be dropped if it is side-effect-free
  ([O0163](O0163-dead-field-elimination.md) has the same requirement).
- Multi-register returns are part of the internal convention
  ([O0282](O0282-internal-calling-convention.md)).
