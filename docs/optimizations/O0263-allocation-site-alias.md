# O0263 — Allocation-site alias analysis

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Analysis infrastructure |
| **Related** | [O0171](O0171-alias-analysis.md), [O0260](O0260-escape-analysis.md), [O0152](O0152-vector-alias-versioning.md) |
| **Split from** | [O0171](O0171-alias-analysis.md) |

## The idea

Two objects created at **different allocation sites** are distinct, and stay
distinct through copies of their descriptors. For PB that means two separately
`DIM`ed dynamic arrays, and two separately allocated strings, never overlap —
even though both live in the same heap and neither has a compile-time address.

That is the fact `c(i) = a(i) + b(i)` needs to vectorize when the arrays are
dynamic rather than static.

## Applies to

```basic
DIM a%(), b%(), c%()
REDIM a%(0 TO 999), b%(0 TO 999), c%(0 TO 999)
FOR i% = 0 TO 999
  c%(i%) = a%(i%) + b%(i%)   ' three distinct allocation sites
NEXT
```

## What it needs

- Tracking a **handle's provenance** through assignment and BYREF passing —
  which is where escape analysis ([O0260](O0260-escape-analysis.md)) supplies
  the boundary conditions.
- `SWAP`, `REDIM PRESERVE` and array-descriptor aliasing must be modelled, or
  the provenance chain breaks silently.
- Where provenance is lost, the runtime range check
  ([O0152](O0152-vector-alias-versioning.md)) is the fallback.
