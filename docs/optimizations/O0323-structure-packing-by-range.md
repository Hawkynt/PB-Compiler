# O0323 — Structure packing by range

| | |
|---|---|
| **Status** | 🟡 Partial — byte-granular integer narrowing implemented; sub-byte coalescing remains planned |
| **Stage** | Whole-program data layout |
| **IR** | 🟡 `Ir/Passes/DataLayoutTransforms.cs` — joins `IrRangeAnalysis` facts for every store to a private integer field, narrows storage to 8/16/32 bits when safe, and inserts truncation/extension at the record boundary |
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

## What it needs

- Per-field range facts across the **whole program**
  ([O0016](O0016-value-fact-analysis.md),
  [O0158](O0158-interprocedural-range-propagation.md)) — one out-of-range write
  anywhere invalidates the packing.
- The cost trade: a packed field costs a shift and a mask per access, so it wins
  for large arrays and loses for hot scalars — the same emitter-versus-analysis
  split [O0057](O0057-storage-narrowing.md) describes.
- Layout-observability rules as in [O0321](O0321-field-reordering.md).

The current IR transform stops at byte-addressable storage. It can turn an
`INTEGER` field proven to stay in `0..7` into an unsigned byte, but it does not
yet coalesce several such ranges into one shared bit-field byte/word.
