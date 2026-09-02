# O0331 — Bitset substitution

| | |
|---|---|
| **Status** | 🟡 Partial — non-escaping zero-initialized `INTEGER` Boolean globals are bit-packed when every access preserves Boolean semantics |
| **Stage** | Whole-program data layout |
| **Source** | `Ir/Passes/BitsetSubstitution.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `DataRepresentationOptimizationTests` |
| **Related** | [O0155](O0155-bit-plane-transformation.md), [O0323](O0323-structure-packing-by-range.md), [O0099](O0099-bit-test-dispatch.md) |

## The idea

An array of Booleans (or of a very small domain) stored one element per
`INTEGER` wastes 15 bits out of 16. Packing it to one bit per element cuts the
storage by 16× — which on a 64 KiB-segment machine can be the difference between
fitting and not — and makes whole-array operations single bitwise instructions.

## Implemented v1

`BitsetSubstitution` rewrites a zero-initialized global `INTEGER` array to byte
storage with one bit per logical element when all uses are visible to the module
pass and every store is exactly `0` or `-1`.

Each read becomes byte-index calculation, mask/test, and reconstruction of PB's
`0`/`-1` Boolean value. Each write becomes a byte read-modify-write. Differently
typed direct accesses, address escapes, error-handler/inline-asm functions, and
non-Boolean stores make the pass decline.

## Applies to

```basic
DIM seen%(0 TO 65535)        ' 128 KB: does not fit a segment at all
seen%(k%) = -1
IF seen%(k%) THEN ...
```

packs to 8 KB; ordinary element access uses shift/mask operations instead of a
16-bit array load/store.

## Still planned

- Small domains wider than one bit.
- Packing locals and other storage classes where whole-program observability is
  not required.
- Cost-model decisions for cases where per-element access dominates storage
  pressure.
- Whole-array operations such as `ERASE` specialized directly to the packed
  representation.
