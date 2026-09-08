# O0280 — Argument structure reduction

| | |
|---|---|
| **Status** | 🟡 Partial — fully visible read-only aggregate-pointer parameters from proven local byte aggregates are scalarized |
| **Stage** | Whole-program IR |
| **Source** | `Ir/Passes/ArgumentStructureReduction.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `PowerBasic.Compiler.Tests/Ir/ArgumentStructureReductionTests.cs` |
| **Related** | [O0069](O0069-dead-parameter-elimination.md), [O0059](O0059-scalar-replacement.md), [O0281](O0281-return-structure-reduction.md), [O0282](O0282-internal-calling-convention.md) |

## The idea

A procedure that takes a whole `TYPE` (or a descriptor) but reads only two of
its fields does not need the aggregate. Passing the fields instead removes the
copy or the indirection at every call site — and turns a BYREF aggregate
parameter into `BYVAL` scalars, which then travel in registers
([O0021](O0021-register-parameters.md)).

## Applies to

```basic
TYPE Rect
  x AS INTEGER
  y AS INTEGER
  w AS INTEGER
  h AS INTEGER
END TYPE

FUNCTION Area%(r AS Rect)    ' reads only w and h
  Area% = r.w * r.h
END FUNCTION
```

becomes `FUNCTION Area%(BYVAL w%, BYVAL h%)` internally when every call is under
compiler control.

## Implemented IR slice

`ArgumentStructureReduction` rewrites an internal function signature and all of
its direct calls together. A pointer formal is eligible when:

- every callee use is a scalar integer/float load at a constant byte offset;
- the loaded byte regions do not overlap, preserving `UNION`/shared-storage
  semantics;
- the function address does not escape and every call site is visible;
- every corresponding actual is a direct local `alloca i8, N`, which is the IR
  shape used for packed aggregate storage and proves the accessed fields fit;
- the callee is a leaf and writes only callee-local storage, so moving the field
  loads to immediately before the call cannot cross a possibly aliasing mutation;
- at most three scalar pieces replace one aggregate parameter, keeping signature
  growth bounded by the same conservative default used by LLVM argument
  promotion.

Repeated loads of one field share one scalar formal. Unchanged formals are
re-created with their new positional indices, and call convention/result identity
is preserved. After the call stops receiving the aggregate pointer, the ordinary
caller-side aggregate SROA pass can often remove the now-non-escaping storage.

The IR currently erases source-level aggregate-kind metadata when a BYREF record
becomes a pointer. Requiring proven local byte-aggregate actuals is therefore
intentional: treating every pointer formal as a record would also rewrite BYREF
scalars, strings, arbitrary pointers, and external storage with no semantic proof.

## Still planned

- Forwarded aggregate parameters and aggregate globals once equivalent provenance
  survives lowering/interprocedural analysis.
- BYVAL record copy-in reduction; the current lowering models that copy explicitly
  and this v1 does not bypass it.
- Callees with calls or nonlocal writes once mod/ref and alias analysis can prove
  that the selected fields remain unchanged until each original load.
- Writable aggregate fields / return-channel reconstruction, which belongs to
  [O0281](O0281-return-structure-reduction.md).
- Richer target-aware profitability beyond the current three-piece cap.

## References and licensing

The implementation is original C# over PB-Compiler's IR. External material was
used only to establish behavior and safety requirements; no implementation code,
comments, naming, or control flow was copied or translated.

- LLVM `argpromotion` documentation and `ArgumentPromotion.cpp`: by-reference
  arguments that are only loaded may be promoted to scalar values; aggregate
  promotion is bounded by a configurable maximum number of scalar pieces. LLVM
  is Apache-2.0 WITH LLVM-exception.
- LLVM Alias Analysis documentation: argument promotion uses alias/mod-ref facts
  to ensure the pointee is not modified before promoted loads.
- GCC optimization documentation for IPA-SRA / scalar replacement of aggregate
  parameters, consulted as an independent behavioral reference. No GCC source
  code was used.
