# O0331 — Bitset substitution

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Whole-program data layout |
| **Related** | [O0155](O0155-bit-plane-transformation.md), [O0323](O0323-structure-packing-by-range.md), [O0099](O0099-bit-test-dispatch.md) |

## The idea

An array of Booleans (or of a very small domain) stored one element per
`INTEGER` wastes 15 bits out of 16. Packing it to one bit per element cuts the
storage by 16× — which on a 64 KiB-segment machine can be the difference between
fitting and not — and makes whole-array operations single bitwise instructions.

## Applies to

```basic
DIM seen%(0 TO 65535)        ' 128 KB: does not fit a segment at all
seen%(k%) = -1
IF seen%(k%) THEN ...
```

packs to 8 KB, and `ERASE` becomes a `REP STOSW` over 8 KB rather than 128 KB.

## What it needs

- Proof that **every** stored value is 0 or −1 (or fits the small domain) across
  the whole program ([O0158](O0158-interprocedural-range-propagation.md)).
- A representation change: each access becomes an index shift, a mask and a bit
  test or a read-modify-write — cheap, but not free, so the trade is size and
  whole-array speed against per-element cost.
- Non-observability, as for every layout change
  ([O0321](O0321-field-reordering.md)).
