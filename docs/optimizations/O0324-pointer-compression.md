# O0324 — Pointer compression

| | |
|---|---|
| **Status** | 🟡 Partial — same-region pointer-array compression implemented |
| **Stage** | Whole-program data layout |
| **IR** | 🟡 `Ir/Passes/DataLayoutTransforms.cs` — when an explicit target pointer width is greater than 16 bits, a private pointer array whose non-null values are all typed GEPs into one proven region is stored as `u16` indices with `65535` as null and reconstructed on load |
| **Related** | [O0323](O0323-structure-packing-by-range.md), [O0057](O0057-storage-narrowing.md), [docs/FORMATS.md](../FORMATS.md) |

## The idea

When every object a pointer can address lies inside a **bounded region**, the
pointer can be stored as a narrower offset or index into that region and widened
only when dereferenced.

On x86-16 this is unusually concrete: a far pointer is 4 bytes (segment +
offset), but if the target is always inside one known segment — the string heap,
the array heap, a single data segment — the segment half is redundant and a
2-byte near offset does the same job. Halving every stored pointer in a large
structure is a real memory saving on a 640 KiB machine.

## Applies to

```basic
TYPE Node
  next AS LONG POINTER       ' 4 bytes, but always into the same segment
  value AS INTEGER
END TYPE
```

## What it needs

- A proof that every value stored into the field points into the known region —
  which is a points-to question ([O0263](O0263-allocation-site-alias.md)).
- Widening on dereference, and correct handling of the null representation.
- `VARPTR32`/`STRPTR32` and any pointer that escapes to an external unit must
  keep the full form.

The current IR implementation covers pointer arrays because their element
provenance is explicit in typed GEPs. Compressing a pointer *field inside an
opaque packed UDT byte buffer* needs equivalent pointer-field provenance from
lowering before it can be done without guessing the source type.
