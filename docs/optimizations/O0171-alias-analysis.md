# O0171 — Alias analysis (basic, type-based, allocation-site)

| | |
|---|---|
| **Status** | 🟨 Basic width-aware IR analysis implemented; type-based and allocation-site layers remain planned |
| **Stage** | Analysis infrastructure |
| **Source** | `Ir/Analysis/IrAliasAnalysis.cs` |
| **Related** | [O0060](O0060-memory-ssa.md), [O0140](O0140-load-store-motion.md), [O0152](O0152-vector-alias-versioning.md), [O0161](O0161-function-summaries.md), [O0172](O0172-loop-dependence-analysis.md) |
| **Split into** | [O0262](O0262-type-based-alias.md), [O0263](O0263-allocation-site-alias.md) |

## The idea

Most advanced optimizations refuse to fire without an answer to "can these two
accesses touch the same byte?". Three layers, in increasing precision:

1. **Basic** — distinguish storage objects and byte ranges. Distinct local
   allocations and distinct globals do not alias; constant GEPs are reduced to
   root + byte offset; the access width decides whether two offsets overlap.
2. **Type-based** — two accesses through incompatible element types cannot
   alias, where BASIC's semantics permit that conclusion (PB has no unions of
   convenience except `UNION` itself, which must be excluded).
3. **Allocation-site** — two independently `DIM`ed dynamic arrays or independently
   allocated strings are distinct objects, and stay distinct through copies of
   their descriptors.

PB is a good candidate: its aliasing entry points are **few and explicit** —
`VARPTR`/`STRPTR` escape, `BYREF` parameters, `@p` stores, `PEEK`/`POKE` after
`DEF SEG`, inline asm, external unit calls — so everything else can eventually
be made precise without pretending arbitrary pointers are independent.

## Implemented basic layer

`IrAliasAnalysis` answers `NoAlias`, `MayAlias`, `PartialAlias` or `MustAlias`
for two typed memory accesses. A query is a pointer **and its access type**: the
width is essential because a two-byte access at offset 0 overlaps a two-byte
access at offset 1 even though the starting pointers differ.

The current provenance model deliberately recognizes only facts carried directly
by the IR:

- distinct `alloca` roots are disjoint;
- distinct global-variable roots are disjoint;
- nested constant byte-offset GEPs are flattened;
- constant element-indexed GEPs are scaled when the element has a
  target-independent width;
- unknown/dynamic offsets, pointer-sized elements, BYREF/loaded pointers, casts
  and explicit far pointers conservatively answer `MayAlias`.

`RedundantMemory` ([O0047](O0047-ir-redundant-memory.md)) and `DeadStoreElim`
([O0048](O0048-ir-dead-store-elimination.md)) are the first consumers. This also
fixes two width bugs in their former private alias tests: a partial overlapping
store now invalidates a cached wider load, and a narrow store at the same start
address no longer kills a wider earlier store unless it covers the whole range.

## Why it matters for the loop family

The next layer is [O0172](O0172-loop-dependence-analysis.md): affine loop-access
analysis needs an oracle for whether two base objects can alias before solving
subscript equations has any meaning. Once that exists, loop interchange, tiling,
distribution/fusion and vector alias versioning can share the same legality facts
instead of each inventing a syntactic proxy.

## Still planned

- Escape-aware allocation-site provenance for dynamic arrays, strings and heap
  objects.
- PB-safe type-based alias rules, with explicit `UNION` exclusions.
- Call Mod/Ref facts from [O0161](O0161-function-summaries.md).
- A memory-SSA consumer layer ([O0060](O0060-memory-ssa.md)) so alias facts can
  drive motion and elimination across basic blocks.
