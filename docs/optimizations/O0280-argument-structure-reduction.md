# O0280 — Argument structure reduction

| | |
|---|---|
| **Status** | 🟡 Partial — read-only aggregate arguments are scalarized across local/global/forwarded provenance, BYVAL copy-in, and mod/ref-safe non-leaf callees |
| **Stage** | Whole-program IR |
| **Source** | `Ir/Passes/ArgumentStructureReduction.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `PowerBasic.Compiler.Tests/Ir/ArgumentStructureReductionTests.cs` |
| **Related** | [O0069](O0069-dead-parameter-elimination.md), [O0059](O0059-scalar-replacement.md), [O0161](O0161-function-summaries.md), [O0171](O0171-alias-analysis.md), [O0281](O0281-return-structure-reduction.md), [O0282](O0282-internal-calling-convention.md) |

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
its direct calls together. A pointer formal is eligible when every use that
survives in the callee is a scalar integer/float load from constant,
non-overlapping byte regions and the function address has not escaped.

Aggregate provenance is reconstructed interprocedurally instead of inferred from
`ptr` alone:

- direct `alloca i8, N` storage is accepted;
- mutable `IrGlobalVariable` byte storage is accepted;
- constant byte/typed GEPs preserve the root and reduce the remaining extent;
- a forwarded pointer formal is accepted only when the compiler sees every call
  to its owner and every incoming actual recursively resolves to concrete
  aggregate storage;
- recursion or a forwarding cycle without a concrete root declines rather than
  inventing provenance.

This matters because lowering intentionally erases source-level UDT identity at
the pointer boundary. A pointer is therefore never promoted merely because it is
a pointer.

### BYVAL copy-in

Lowering represents a BYVAL UDT parameter as an incoming pointer followed by an
entry `llvm.memcpy.p0.p0.i32` into a private `alloca i8, N`. O0280 recognizes
that exact non-volatile copy-in contract. When the private copy is only read at
eligible constant field offsets, the caller loads those fields before the call,
the scalar values become the callee's snapshot, and the entry memcpy/private
aggregate disappear.

The full copy size still participates in the provenance/bounds proof: replacing a
four-byte copy by one two-byte field load does not let an unproven two-byte
pointer masquerade as the original four-byte BYVAL record.

The pass runs after the ordinary local scalar-replacement sweep. That sweep only
breaks a BYVAL memcpy apart when the surrounding typed accesses prove a complete
layout of the copied record; the partial-field case O0280 targets therefore keeps
its explicit entry copy and remains recognizable here.

### BYREF mod/ref proof

BYREF fields are different: moving their loads to the call site is legal only if
nothing the callee executes can change the selected bytes first. The pass checks
that per visible call site using concrete roots and byte ranges.

- Direct stores are compared with the selected field locations. Stores to a
  distinct local/global object are harmless; stores through another formal are
  accepted only when its actuals resolve to disjoint concrete storage.
- Calls already proven non-writing by `FunctionSummaries` are ignored.
- Writable direct internal callees are inspected recursively with their pointer
  formals bound to the caller's concrete locations. This permits, for example, a
  helper that writes only to a separate scratch local or a different global.
- Indirect calls, opaque/external writers, error-handler bodies, inline assembly,
  unresolved pointer provenance, and writable recursive cycles remain barriers.

The check is deliberately stronger than necessary: it rejects a possible write
anywhere in the dynamic call, even when a full CFG ordering proof could show the
write happens only after the last selected load. That keeps the first
location-sensitive mod/ref implementation auditable.

The module pass runs to a finite structural fixpoint: scalarizing a leaf can
expose the forwarding wrapper above it as another load-only aggregate parameter,
so visible forwarding chains collapse without depending on function order.

Repeated loads of one field share one scalar formal. Unchanged formals are
re-created with their new positional indices, call convention/result identity is
preserved, and at most three scalar pieces replace one aggregate parameter. The
three-piece cap is PB-Compiler's current target-neutral profitability policy;
LLVM likewise exposes a configurable aggregate-expansion limit, although LLVM
24's `ArgumentPromotionPass` constructor currently defaults `MaxElements` to two
(the source-file overview still describes the historical three-operand default).

## Still planned

- Pointer provenance through dynamic GEPs, loaded pointers, integer/pointer
  round-trips, far pointers, and other opaque address-producing forms.
- Richer per-external-call memory effects so an impure runtime call that provably
  cannot touch the selected object need not remain a wall.
- Descriptor forms whose lowering is not a byte aggregate with constant field
  offsets.
- Writable aggregate fields / return-channel reconstruction, which belongs to
  [O0281](O0281-return-structure-reduction.md).
- Target-aware profitability beyond the current fixed three-piece cap.

## References and licensing

The implementation is original C# over PB-Compiler's IR. External material was
used only to establish behavior and safety requirements; no implementation code,
comments, naming, or control flow was copied or translated.

- LLVM `ArgumentPromotion.cpp` / `ArgumentPromotionPass`: internal by-reference
  arguments may be replaced by direct scalar arguments when loaded parts are
  safe, callers are controlled, and aggregate expansion stays within a
  profitability limit. LLVM is Apache-2.0 WITH LLVM-exception.
- LLVM Alias Analysis documentation: argument promotion relies on conservative
  alias/mod-ref answers to prove a promoted location is not modified before the
  original load.
- GCC IPA-SRA / `-fipa-modref` documentation: interprocedural scalar replacement
  and memory-effect analysis provide the same broad behavioral reference. GCC
  material was used as an independent oracle only; no GCC implementation code
  was reused.
