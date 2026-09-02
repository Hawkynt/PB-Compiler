# O0059 — Scalar replacement of aggregates (SROA)

| | |
|---|---|
| **Status** | 🟨 Partial — independent local fields plus proven whole-record copy/integer-equality decomposition implemented |
| **Stage** | Mid-end, IR byte-region analysis |
| **Source** | `Ir/Passes/AggregateBlockScalarization.cs`, `Ir/Passes/ScalarReplaceAggregates.cs`, followed by `Mem2Reg` |
| **Gate** | Standard optimized IR pipeline |
| **Related** | [O0015](O0015-udt-zero-cost.md), [O0058](O0058-386-register-allocation.md), [O0011](O0011-literal-overlap-pooling.md) (shared escape-analysis direction) |

## The idea

UDT lowering starts from a representation that is deliberately simple and always correct: a packed
`alloca i8, N` plus typed loads and stores at constant byte offsets. Whole-value assignment and BYVAL
entry copies use `llvm.memcpy`; whole-value `=`/`<>` uses `rt_mem_compare` so the initial IR preserves
the record's actual bytes rather than inventing higher-level field semantics.

The optimized pipeline now has two aggregate stages:

1. `AggregateBlockScalarization` examines whole-record copies/comparisons. It recovers the typed regions
   observed around local packed records and proceeds only when those regions form a complete, gap-free,
   non-overlapping partition of the exact copied/compared extent.
2. `ScalarReplaceAggregates` then handles the resulting ordinary field loads/stores. Independent regions
   become typed scalar allocas; the immediately following `Mem2Reg` sweep promotes those slots into SSA
   when normal data flow permits it.

That gives O0059 these implemented cases:

- **Scalar replacement** — independent fields become ordinary scalar values and can register-allocate,
  constant-propagate, value-number, and die like handwritten locals;
- **Whole-record copy decomposition** — a proven complete layout turns `memcpy` into scalar loads at the
  original copy point plus scalar stores. Later SROA/mem2reg can then remove the materialized records;
- **BYVAL snapshot scalarization** — when all bytes are proven by independent scalar fields, the entry
  copy becomes scalar loads from the incoming record pointer at the original copy point, preserving
  the entry snapshot while removing the block-copy temporary;
- **Integer-only whole-record equality** — `rt_mem_compare(...) ==/!= 0` becomes conjunction/inversion
  of per-region integer equality when every compared byte belongs to a proven integer region;
- **No aggregate runtime representation** — the optimization introduces no descriptors, tags, boxing,
  dictionaries, or dispatch machinery;
- **Conservative union handling** — distinct overlapping regions keep their shared backing storage,
  preserving `UNION` aliasing/type-punning semantics.

## Applies to

```basic
TYPE Vec
  x AS INTEGER
  y AS INTEGER
END TYPE

FUNCTION CopyAndDot%(BYVAL ax%, BYVAL ay%, BYVAL bx%, BYVAL by%)
  DIM a AS Vec, b AS Vec
  a.x = ax% : a.y = ay%
  b = a
  IF b.x = bx% THEN b.y = by%
  CopyAndDot% = b.x * bx% + b.y * by%
END FUNCTION
```

The source record `a`, destination record `b`, and whole-value copy can all disappear when the typed
observations prove both two-byte fields and therefore all four bytes of `Vec`. The copy semantics do
not disappear: scalar loads are placed at the original copy point first, and later SSA/data-flow
optimization removes storage only after the values themselves carry the same snapshot.

The same path applies to a PB 3.6 generic `TYPE` after monomorphization: the generic template is
already cloned into a concrete UDT before IR lowering, so aggregate analysis sees no generic machinery
at all.

## Byte semantics and the safety boundary

The proof is intentionally narrower than ordinary source-level field reasoning:

- dynamic offsets decline;
- out-of-bounds accesses decline;
- nested/escaping pointers decline;
- target-width pointer fields decline for now because their storage width is not target-independent in
  `IrType`;
- layouts with unobserved gaps/padding decline for whole-value decomposition because those bytes are
  observable to the original copy/comparison;
- whole-object operations with dynamic size, volatile copy, unknown storage, or non-equality users
  decline;
- two distinct accessed regions that overlap decline. This is the rule that keeps `UNION` correct;
- floating whole-record equality stays on `rt_mem_compare`. IEEE `fcmp` would make `+0` equal `-0` and
  treats NaNs according to numeric rules, while PowerBASIC's current UDT equality lowering compares raw
  bytes. Replacing one with the other would be wrong even though both are spelled "equality".

Whole-record copy decomposition may include floating scalar regions because a typed load followed by a
typed store preserves the stored representation without performing floating arithmetic. Equality has a
tighter boundary because the comparison operation itself can reinterpret the bytes semantically.

There is a separate `ScalarReplaceArrays` pass for homogeneous small arrays. It additionally proves
that every access has the array element's storage type. This matters because packed UDT backing also
looks like `alloca i8, N`; without the access-width proof an INTEGER field inside a packed record could
be mistaken for one BYTE array element under opaque pointers.

## Still partial

O0059 remains partial rather than complete because useful cases still require stronger proofs or a raw
storage representation:

- floating-region whole-record equality needs a bit-preserving comparison form (for example a proven
  same-width raw-bit cast) before it can replace byte comparison;
- pointer-containing aggregates need target-aware storage widths/address-space rules;
- copies with bytes that have no typed observation currently remain whole-object copies, even when
  field-granular liveness could prove those particular bytes dead;
- escaping/nested aggregate addresses remain materialized;
- aligned records with observable padding remain byte-backed unless every byte is represented in the
  proof.

Those are optimization opportunities, not permission to weaken the language's existing byte and
aliasing semantics.
