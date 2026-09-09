# O0305 — Basic-block versioning

| | |
|---|---|
| **Status** | 🟡 Partial — bounded multi-block regions, multi-context fallback, range/alignment guard specialization, and local SSA exit repair are implemented; general multi-exit SSA and memory-alignment metadata remain planned |
| **Stage** | Mid-end |
| **Source** | `Ir/Passes/BasicBlockVersioning.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `BasicBlockVersioningTests` |
| **Related** | [O0304](O0304-guarded-specialization.md), [O0156](O0156-path-sensitive-propagation.md), [O0107](O0107-branch-folding-through-phi.md), [O0139](O0139-alignment-versioning.md), [O0225](O0225-ssa-construction.md) |

## The idea

Create specialized copies of a CFG **region** for different fact sets — a range,
an alignment, a known value — and route execution into the right one. Where
path-sensitive propagation ([O0156](O0156-path-sensitive-propagation.md)) keeps
the facts *in the analysis*, versioning materializes them **in the code**, so
every downstream pass sees a region where the fact simply holds.

## Implemented

The pass starts from a conditional whose two guarded paths later reconverge. A
specialized copy is created only for an outcome that makes a later guard
provably constant. Contexts that do not buy a simplification continue to use the
original code, which remains the fully general fallback.

Additional predecessors are therefore safe rather than a reason to reject the
transform. If a third, fourth, or later path reaches the reconvergence without
the versioned guard fact, those edges remain attached to the original region.
Phi nodes at the region entry retain exactly the incoming contexts that still
reach that version.

### Bounded multi-block regions

O0305 is no longer limited to a guard in the first reconverged block. Starting
at the join, it considers increasingly long **single-entry forward chains**:

- the shortest one-block region is tried first, preserving the original
  transform and minimizing code growth;
- an interior block may be added only when the previous block branches to it
  unconditionally and it has exactly that one predecessor;
- back edges and address-taken blocks terminate the region;
- at most four blocks are considered;
- the complete source region must still fit the 32-instruction per-copy budget.

The shortest prefix with a concrete simplification wins. Thus a lowering shape
such as

```text
join:
  br check
check:
  %again = cmp x, 256
  br %again, small, large
```

can be specialized even though `join` itself contains no useful guard. Both
`join` and `check` are copied so the path fact remains materialized until the
redundant comparison is reached.

This is intentionally a bounded SESE-like forward slice rather than arbitrary
region cloning. A block with another incoming edge is a new context boundary;
including it without a second entry model would make the clone reachable under
facts it did not establish.

### Known-value facts

A direct reuse of the branch condition, or a structurally identical repeated
integer comparison, is bound to the selected branch result. Ordinary
`SimplifyCfg`/DCE then removes the redundant check and unreachable arm.

Direct condition reuse is recognized anywhere in the accepted forward region,
including an `IrSelect`, so a value computed after one or more bridge blocks can
still benefit from the version.

### Integer range facts

For integer `value op constant` guards, O0305 consumes the existing
`IrRangeAnalysis` result at the guard and intersects it with the selected edge.
A later comparison of the same SSA value against another constant can therefore
fold even when it is not textually the same test.

For example, after `x < 256`:

- the true context proves `x < 512`;
- the false context proves `x < 128` is false;
- neither context is invented when the intervals still overlap the later test.

Signed and unsigned predicates keep the same conservative interval rules as the
range-analysis consumers: an unsigned interpretation is declined when the
known mathematical interval may be negative.

### Pointer-alignment facts

The target-neutral IR does not yet attach alignment metadata to loads/stores;
the LLVM emitter intentionally emits `align 1` for every access. O0305 therefore
does **not** claim a memory operation is aligned merely because one path checked
it.

It can, however, remove redundant alignment guards. The canonical low-bit form

```text
(ptrtoint p AND (2^k - 1)) == 0
```

is recognized for `== 0` and `!= 0`. The implications are exact bit facts:

- 16-byte aligned implies 8-, 4- and 2-byte aligned;
- not 8-byte aligned implies not 16-, 32-, ... aligned;
- the reverse implications are deliberately not assumed.

Only masks exactly equal to `2^k - 1` qualify, and the same pointer SSA value
must underlie both checks.

## SSA repair across versions

Cloning a block that defines no externally used value is local. Once a version
adds a new CFG predecessor or a second definition of a value, SSA has to be
repaired explicitly.

The current pass handles the two local cases for which the required SSA mapping
is exact and cheap.

### Successor phis

`IrCloner` deliberately clones only the requested region. An outside successor
is therefore still the original block, and every phi in it needs a new incoming
for each cloned predecessor.

For every region edge `source -> successor`, O0305 copies the successor phi's
incoming from `source`, maps that value through the clone's value map, and adds
it under the cloned predecessor. If the original region becomes unreachable,
its old incoming is removed before the original blocks are deleted.

This follows the same structural requirement documented for LLVM
`CloneBasicBlock`: cloning a block does not repair successor phis automatically;
the caller that creates the new CFG edge must do so.

### Directly escaping definitions

A definition used directly after the versioned region gives the logical value
multiple SSA definitions — one in the generic region and one in every retained
specialization.

O0305 repairs this when the accepted forward region has one exact continuation:

1. the region tail is an unconditional branch to `exit`;
2. `exit` had exactly that tail as its original predecessor;
3. `exit` dominates every non-boundary use of the escaping definition.

A phi is inserted in `exit` with one incoming for the generic definition when
the fallback survives and one mapped incoming per specialized copy. Outside
uses are redirected to that merged value. This also handles the important
one-sided case where, for example, only the `x >= 256` context proves a later
`x < 128` test false: the exit phi merges the original general result with the
false-context result.

The pass still declines a directly escaping value when the region has multiple
exits. Correct repair there needs a general SSA updater / iterated dominance
frontier placement rather than a local exit phi, and silently choosing one
successor as the merge point would miscompile uses reached through the others.

## Applies to

A repeated branch remains the simplest visible case:

```basic
IF x% < 256 THEN
  t% = 1
ELSE
  t% = 2
END IF

IF x% < 256 THEN PRINT t%
```

Both guarded contexts are profitable, so the original join disappears and two
specialized versions replace it.

A range implication can justify only one version:

```basic
IF x% < 256 THEN
  t% = 1
ELSE
  t% = 2
END IF

IF x% < 128 THEN PRINT t%
```

The `x >= 256` context knows the second test is false. The `x < 256` context does
not know whether `x < 128`, so that edge keeps using the original general region.

The guard may now live farther into a forward region, and a value produced there
may flow to a common exit. In that case the specialized and general definitions
are rejoined by an SSA phi before normal downstream optimization continues.

## Safety boundaries still in place

The pass still declines:

- loop headers/back-edge reconvergences;
- address-taken blocks and functions with hidden error/inline-asm control flow;
- an interior region block with any external predecessor;
- a directly escaping SSA definition when the versioned region has multiple
  exits or no unique dominating merge continuation;
- parallel boundary edges to one successor, because this IR's phis are indexed
  by predecessor block rather than individual CFG edge;
- regions above four blocks or the hard 32-instruction code-growth budget.

These are not semantic limitations of BBV; they are the boundary of the current
local region and SSA rewrite.

## Still planned

- General multi-exit SSA repair, ideally as a reusable SSA updater driven by the
  existing `IrDominators.FrontierOf` infrastructure.
- Richer single-entry regions with internal branches/reconvergence instead of a
  forward chain only.
- First-class IR alignment facts on memory operations, enabling downstream
  vector/wide-access selection instead of merely deleting redundant alignment
  tests.
- Non-null, alias/dependence and compound fact contexts where existing analyses
  can prove them.
- Profitability tied to target costs, profile information, vectorization and
  operation/storage narrowing.
- A larger context/version budget when target or profile evidence justifies the
  code growth.

## References

The implementation is clean-room C# over PB-Compiler's existing `IrCloner`,
`IrRangeAnalysis`, `IrDominators` and SSA APIs; no external implementation code
was copied or translated.

- Chevalier-Boisvert & Feeley, *Simple and Effective Type Check Removal through
  Lazy Basic Block Versioning*, ECOOP 2015 — context-specialized blocks under a
  per-block version limit.
- Melançon, Feeley & Serrano, *Static Basic Block Versioning*, ECOOP 2024 —
  ahead-of-time BBV, bounded versions, and retaining only specializations that
  improve generated code. The paper is published under CC BY 4.0.
- LLVM `CloneBasicBlock` / `Cloning.h` — consulted for CFG/SSA cloning mechanics;
  its documentation explicitly requires callers to repair cloned phis and
  successor-phi incoming edges. LLVM is Apache-2.0 WITH LLVM-exception.
- LLVM basic-block path cloning — consulted as an architectural example of
  cloning a path by redirecting an entering edge and remapping its copied
  blocks; no implementation code was copied.
- PB-Compiler's own [O0225](O0225-ssa-construction.md) / `Mem2Reg` — the existing
  Cytron-style dominance-frontier construction remains the intended basis for a
  later general multi-exit SSA updater rather than duplicating a new analysis in
  O0305.
