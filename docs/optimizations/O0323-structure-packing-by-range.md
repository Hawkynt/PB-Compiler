# O0323 — Structure packing by range

| | |
|---|---|
| **Status** | ✅ Implemented for private fixed-size exact packed scalar record arrays |
| **Stage** | Whole-program data layout |
| **IR** | ✅ `Ir/Passes/DataLayoutTransforms.cs` — joins `IrRangeAnalysis` facts for every store to a private integer field, narrows byte-addressable storage when safe, and coalesces proven 1–7-bit fields into shared bytes with explicit shift/mask accesses |
| **Verified by** | `DataLayoutTransformsTests`, `StructurePackingByRangeTests` |
| **Related** | [O0057](O0057-storage-narrowing.md), [O0016](O0016-value-fact-analysis.md), [O0321](O0321-field-reordering.md) |

## The idea

A field whose values provably fit fewer bits is **stored** in fewer bits. It is
[O0057](O0057-storage-narrowing.md) applied to aggregate members, where the
payoff is multiplied by the number of instances — and where `pb36` already has
the storage form: bit-fields (`Mode AS BIT * 3`), which the binder desugars into
shift-and-mask accesses over a hidden word.

## Applies to

```basic
TYPE Tile
  kind AS INTEGER            ' only ever 0..7
  flags AS INTEGER           ' only ever 0..3
END TYPE
DIM map(0 TO 63, 0 TO 63) AS Tile   ' 16 KB, of which 5 bits per record matter
```

packs to a single byte per record.

## Implementation

The transform operates only on private, non-escaping packed record arrays whose
field boundaries are recovered exactly from affine byte GEPs. For each integer
field it joins the range at every store together with the language-defined
zero-initialized state. A single unknown or out-of-range write therefore widens
the proven range and prevents an unsafe representation change.

Ranges requiring 8, 16, or 32 bits keep the existing byte-granular narrowing.
Ranges requiring 1–7 bits are assigned consecutive slices of byte-addressable
`i8` storage. A field never straddles a byte: if the next slice would cross the
boundary, packing resumes at the following byte. Loads extract the slice with a
logical shift and mask, restore the sign for signed ranges, and widen back to the
source type. Stores use a read-modify-write mask so neighbouring packed fields
retain their bits.

The representation stays byte-addressable deliberately. It does not emit
non-byte-sized memory loads/stores; only the value extraction itself is
sub-byte.

## Safety boundaries

- Per-field range facts come from [O0016](O0016-value-fact-analysis.md); all
  visible stores participate in the proof.
- Escaping pointers, differently shaped accesses, overlapping fields/`UNION`
  storage, padding gaps, and otherwise observable layouts are rejected.
- Signed ranges use their minimum two's-complement width and are sign-extended
  on load.
- The transform preserves source field order; [O0321](O0321-field-reordering.md)
  remains responsible for field-order changes.

The current cost model is intentionally storage-driven: once the private-layout
proof succeeds and the physical record becomes smaller, packing is accepted.
Back ends may still choose target-specific scalar narrowing policies where
[O0057](O0057-storage-narrowing.md) applies outside aggregate storage.
