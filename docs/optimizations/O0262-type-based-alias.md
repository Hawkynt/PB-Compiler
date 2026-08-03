# O0262 — Type-based alias analysis

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Analysis infrastructure |
| **Related** | [O0171](O0171-alias-analysis.md), [O0060](O0060-memory-ssa.md) |
| **Split from** | [O0171](O0171-alias-analysis.md) |

## The idea

Two accesses through incompatible **element types** cannot touch the same
storage — an `INTEGER` array element and a `SINGLE` array element are never the
same byte, unless the program deliberately overlays them.

BASIC makes this easier to justify than C does: there is no arbitrary pointer
casting, and the deliberate overlays are all *named* — `UNION`, `DIM … AT`,
`FIELD`, `LSET`, and pointer dereferences with an explicit type.

## Applies to

```basic
DIM a%(0 TO 99), f!(0 TO 99), i%
FOR i% = 0 TO 99
  a%(i%) = a%(i%) + 1        ' cannot alias f!(), whatever the indices
  f!(i%) = f!(i%) * 2.0
NEXT
```

## What it needs

- A type-compatibility relation over PB's scalar and aggregate types, with the
  overlay constructs as **explicit exclusions** rather than assumptions.
- Care with `UNION` and `DIM … AT`, where two different types *do* name the same
  storage by design — those must poison the analysis for the storage involved.
