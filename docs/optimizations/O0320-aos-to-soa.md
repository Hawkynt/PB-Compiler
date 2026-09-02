# O0320 — Array of structs → struct of arrays

| | |
|---|---|
| **Status** | ✅ Implemented for private fixed-size packed scalar record arrays |
| **Stage** | Whole-program data layout |
| **IR** | ✅ `Ir/Passes/DataLayoutTransforms.cs` — recovers affine record stride/field offsets from byte GEPs, rejects escaping or overlapping storage, then replaces the record buffer with one typed array per field |
| **Related** | [O0059](O0059-scalar-replacement.md), [O0163](O0163-dead-field-elimination.md), [O0026](O0026-auto-vectorization.md), [O0144](O0144-interleaved-access-vectorization.md) |

## The idea

`DIM p(0 TO 9999) AS Particle` interleaves the fields, so a loop touching only
`x` strides by the record size and drags the other fields through memory with
it. Transposing the storage into separate `x()`, `y()`, `z()` arrays makes each
field contiguous — which is what turns a strided loop into a vectorizable one.

## Applies to

```basic
TYPE Particle
  x AS INTEGER
  y AS INTEGER
  vx AS INTEGER
  vy AS INTEGER
END TYPE
DIM p(0 TO 9999) AS Particle, i%
FOR i% = 0 TO 9999
  p(i%).x = p(i%).x + p(i%).vx     ' stride 8, two fields of four used
NEXT
```

## What it needs

- The array must not **escape** in a way that exposes its layout
  ([O0260](O0260-escape-analysis.md)): no `VARPTR`, no file `GET`/`PUT`, no
  `FIELD`, no whole-record copy to an external unit, no inline asm.
- Whole-record operations (`p(i) = q(j)`, a record compare) become field-wise —
  correct, but the cost model must account for it.
- The transposition is a **representation change**, so every access site,
  `REDIM`, `ERASE` and bounds check has to follow it.

The IR implementation deliberately takes the narrow safe subset: fixed-size stack
storage, scalar field loads/stores, affine byte offsets, and no opaque use of the
record pointer. Dynamic/far arrays and whole-record operations therefore decline
rather than exposing a representation mismatch.
