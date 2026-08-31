# O0059 — Scalar replacement of aggregates (SROA)

| | |
|---|---|
| **Status** | 🟨 Partial — independent local field regions implemented; whole-value copy/compare decomposition pending |
| **Stage** | Mid-end, IR byte-region analysis |
| **Source** | `Ir/Passes/ScalarReplaceAggregates.cs`, followed by `Mem2Reg` |
| **Gate** | Standard optimized IR pipeline |
| **Related** | [O0015](O0015-udt-zero-cost.md), [O0058](O0058-386-register-allocation.md), [O0011](O0011-literal-overlap-pooling.md) (shared escape-analysis direction) |

## The idea

UDT lowering starts from a representation that is deliberately simple and always correct: a packed
`alloca i8, N` plus typed loads and stores at constant byte offsets. That is already a direct layout,
but the GEPs hide field independence from `Mem2Reg`.

`ScalarReplaceAggregates` now recognizes the safe subset: a local byte-backed aggregate whose address
does not escape and whose every observed access names a static, in-bounds, non-overlapping scalar
region. It replaces those regions with typed scalar allocas; the immediately following `Mem2Reg`
sweep promotes them into SSA when normal data flow permits it.

That gives the first half of O0059 today:

- **Scalar replacement** — independent fields become ordinary scalar values and can register-allocate,
  constant-propagate, value-number, and die like handwritten locals;
- **No aggregate runtime representation** — the optimization introduces no descriptors, tags, boxing,
  dictionaries, or dispatch machinery;
- **Conservative union handling** — distinct overlapping regions keep their shared backing storage,
  preserving `UNION` aliasing/type-punning semantics.

The remaining halves are still separate work:

- **Copy elision/decomposition** — whole-UDT assignment still has whole-object identity and therefore
  keeps the existing block-copy path unless another established optimization proves it redundant;
- **Compare lowering** — whole-value `=`/`<>` still keeps the existing bytewise comparison semantics.

## Applies to

```basic
TYPE Vec
  x AS INTEGER
  y AS INTEGER
END TYPE

FUNCTION Dot%(BYVAL ax%, BYVAL ay%, BYVAL bx%, BYVAL by%)
  DIM a AS Vec, b AS Vec
  a.x = ax% : a.y = ay%
  b.x = bx% : b.y = by%
  Dot% = a.x * b.x + a.y * b.y
END FUNCTION
```

When neither aggregate escapes and no whole-object operation observes its packed identity, `a.x`,
`a.y`, `b.x`, and `b.y` become independent typed slots and are then eligible for SSA promotion.

The same path applies to a PB 3.6 generic `TYPE` after monomorphization: the generic template is
already cloned into a concrete UDT before IR lowering, so aggregate SROA sees no generic machinery at
all.

## Safety boundary

The byte-region proof is intentionally narrow:

- dynamic offsets decline;
- out-of-bounds accesses decline;
- nested/escaping pointers decline;
- target-width pointer fields decline for now because their storage width is not target-independent in
  `IrType`;
- whole-object users such as copy/compare/file transfer decline;
- two distinct accessed regions that overlap decline. This is the rule that keeps `UNION` correct.

There is a separate `ScalarReplaceArrays` pass for homogeneous small arrays. It now additionally proves
that every access has the array element's storage type. This matters because packed UDT backing also
looks like `alloca i8, N`; without the access-width proof an INTEGER field inside a packed record could
be mistaken for one BYTE array element under opaque pointers.

## Next steps

- Decompose whole-record copies only when field liveness/escape proof makes that observably identical.
- Lower whole-record equality to field-wise comparisons only when padding/layout and embedded runtime
  types preserve the language's byte-comparison semantics.
- Reuse a richer field-granular escape analysis when O0011/O0059 share enough cases to justify one.
