# O0331 — Bitset substitution

| | |
|---|---|
| **Status** | 🟡 Partial — non-escaping zero-initialized `INTEGER` Boolean globals are bit-packed when every access preserves Boolean semantics |
| **Stage** | Whole-program data layout |
| **Source** | `Ir/Passes/BitsetSubstitution.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `DataRepresentationOptimizationTests`, `BitsetSubstitutionTests` |
| **Related** | [O0155](O0155-bit-plane-transformation.md), [O0323](O0323-structure-packing-by-range.md), [O0099](O0099-bit-test-dispatch.md) |

## The idea

An array of Booleans (or of a very small domain) stored one element per
`INTEGER` wastes 15 bits out of 16. Packing it to one bit per element cuts the
storage by 16× — which on a 64 KiB-segment machine can be the difference between
fitting and not — and makes whole-array operations single bitwise instructions.

## Implemented

`BitsetSubstitution` rewrites a zero-initialized global `INTEGER` array to byte
storage with one bit per logical element when all uses are visible to the module
pass and every store is known to preserve PowerBASIC's `0`/`-1` Boolean domain.
Literal stores are recognized directly; computed stores use the existing SSA
integer range analysis and qualify only when the complete proven interval is
contained in `[-1, 0]`.

Each read becomes byte-index calculation, mask/test, and reconstruction of PB's
`0`/`-1` Boolean value. A literal write uses the cheaper set/clear form. A
range-proven computed Boolean is truncated to its low byte and masked into the
selected bit after clearing the previous bit, so both `0` and `-1` retain their
exact meaning without introducing control flow.

An exact non-volatile full-array zero `llvm.memset.p0.i32` is also preserved:
the call is retargeted to the packed global and its byte count is reduced to the
new representation size. This covers the IR form used for whole-array `ERASE`.
Partial fills, non-zero fills, differently typed direct accesses, address
escapes, error-handler/inline-asm functions, and stores whose Boolean domain
cannot be proved make the pass decline.

## Applies to

```basic
DIM seen%(0 TO 65535)        ' 128 KB: does not fit a segment at all
seen%(k%) = (value% > 0)
IF seen%(k%) THEN ...
```

packs to 8 KB; ordinary element access uses shift/mask operations instead of a
16-bit array load/store. The computed comparison store is eligible because its
result is provably exactly `0` or `-1`.

## Still planned

- Small domains wider than one bit.
- Packing locals and other storage classes where whole-program observability is
  not required.
- Cost-model decisions for cases where per-element access dominates storage
  pressure.
- Additional whole-array operations beyond exact zeroing when their semantics
  can be preserved on the packed representation.
