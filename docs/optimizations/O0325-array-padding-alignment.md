# O0325 — Array padding for alignment

| | |
|---|---|
| **Status** | ✅ Implemented for private scalar IR arrays when target layout facts are supplied |
| **Stage** | Data layout |
| **IR** | ✅ `Ir/Passes/DataLayoutTransforms.cs` + `Ir/Passes/ArrayBaseAlignment.cs` — rounds the physical tail to a whole vector, over-allocates one additional vector, and rewrites uses through a vector-aligned interior pointer |
| **Related** | [O0139](O0139-alignment-versioning.md), [O0026](O0026-auto-vectorization.md), [O0252](O0252-safe-overread-versioning.md) |

## The idea

Two cheap layout choices remove two whole classes of run-time work:

1. **Align the base** of an array to the vector width, so no peeling loop is
   needed ([O0139](O0139-alignment-versioning.md));
2. **Round the length up** to a whole number of vectors, so the tail is a full
   vector of padding rather than a scalar remainder — and a widened load past the
   last real element is provably safe
   ([O0252](O0252-safe-overread-versioning.md)).

The current IR implementation deliberately restricts this to private scalar
`IrAlloca` storage. Escaped/BYREF-visible arrays are left alone because changing
their physical layout could make the padding or adjusted base observable.

## Applies to

```basic
$CPU 80586 MMX
DIM a%(0 TO 999)             ' 1 000 elements: 250 MMX vectors exactly, if aligned
```

## Representation

`IrAlloca` is target-neutral and intentionally has no backend-specific alignment
metadata. O0325 therefore expresses the guarantee with ordinary IR instead of
adding a promise that some emitters could silently ignore:

1. the tail-padding half rounds the logical backing count to complete vectors;
2. the base-alignment half adds one more complete vector of private storage;
3. `ptrtoint` exposes the low address bits using the target pointer width;
4. `(-address) & (vectorBytes - 1)` computes the forward adjustment; and
5. a byte GEP produces the aligned interior pointer used by every original
   access.

Because the backing allocation is itself a whole-vector size and carries an
`.aligned.storage` marker, the pass is stable when the pass manager runs to a
fixpoint. The extra physical storage is less than two vectors relative to the
source array: less than one vector of tail padding plus one vector reserved for
base adjustment.

For the 16-bit DOS target the transform conservatively accepts alignments up to
16 bytes. Real-mode segment bases are paragraph-aligned, so an offset aligned to
8 or 16 bytes is also a linearly aligned address; wider alignment would require
an additional target fact and is rejected rather than guessed.

## Safety rules

- Padding and alignment apply only when the complete pointer-use tree is private
  GEP/load/store traffic; calls, BYREF escape and pointer storage reject the
  transform.
- Element size must divide the vector width and the vector width must be a power
  of two.
- Pointer arithmetic uses an explicitly supplied 16-, 32- or 64-bit target
  pointer width; unsupported layouts decline.
- `UBOUND`, `ERASE`, record/file sizes and all source-visible bounds continue to
  describe the original array. Only private physical storage changes.
- Count arithmetic is checked; an allocation whose alignment slack would
  overflow the IR count is left unchanged.

The implementation is target-gated through `IrDataLayoutTarget`. As with the
other target-dependent O0324–O0326 passes, no target facts are guessed when a
caller does not supply them.
