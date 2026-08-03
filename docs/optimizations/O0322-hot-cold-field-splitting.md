# O0322 — Hot/cold field splitting

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Whole-program data layout |
| **Related** | [O0321](O0321-field-reordering.md), [O0320](O0320-aos-to-soa.md), [O0163](O0163-dead-field-elimination.md) |

## The idea

Separate the frequently used fields from the large, rarely used ones: the hot
record shrinks, so more of them fit per cache line or per 64 KiB segment, and the
cold part is fetched only when touched.

For a DOS target the binding constraint is usually **the 64 KiB segment**, not
the cache: halving a record's hot size doubles how many entities fit in an array
at all.

## Applies to

```basic
TYPE Entity
  x AS INTEGER               ' hot
  y AS INTEGER               ' hot
  description AS STRING * 128 ' cold, and 128 of the record's 132 bytes
END TYPE
DIM e(0 TO 400) AS Entity    ' 52 KB: nearly a whole segment
```

Split, the hot array is 1.6 KB and the cold one is paged or heap-allocated.

## What it needs

- The same escape and layout-observability rules as
  [O0321](O0321-field-reordering.md).
- A **linking mechanism** between the two halves — a shared index — plus the
  cost of the second indirection on every cold access.
- Access-frequency data; without a profile, "large fixed-length string" versus
  "scalar" is already a usable static heuristic.
