# O0305 — Basic-block versioning

| | |
|---|---|
| **Status** | 🟡 Partial — multi-context fallback plus integer-range and pointer-alignment guard specialization implemented; multi-block regions and memory-alignment metadata remain planned |
| **Stage** | Mid-end |
| **Source** | `Ir/Passes/BasicBlockVersioning.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `BasicBlockVersioningTests` |
| **Related** | [O0304](O0304-guarded-specialization.md), [O0156](O0156-path-sensitive-propagation.md), [O0107](O0107-branch-folding-through-phi.md), [O0139](O0139-alignment-versioning.md) |

## The idea

Create specialized copies of a CFG **region** for different fact sets — a range,
an alignment, a known value — and route execution into the right one. Where
path-sensitive propagation ([O0156](O0156-path-sensitive-propagation.md)) keeps
the facts *in the analysis*, versioning materializes them **in the code**, so
every downstream pass sees a region where the fact simply holds.

## Implemented

The current pass versions a small reconverged basic block. A conditional splits
execution into two guarded contexts and those paths later reach one join. O0305
creates a specialized copy only for an outcome that makes a later guard
provably constant. Contexts that do not buy a simplification continue to use the
original block, which is therefore the always-correct generic fallback.

This makes additional predecessors safe rather than a reason to reject the
transform. If a third, fourth, or later path reaches the join without the
versioned guard fact, those edges remain attached to the original block. Phi
nodes in a specialized copy retain only the incoming value for that copy's
edge; the original phi drops only the incoming edges that were rerouted to
specialized versions.

The per-copy source-block budget remains 32 IR instructions and there are at
most two specialized copies, one for each branch outcome. Importantly, a fact
that helps only one outcome creates only one copy. Static code growth is spent
on an observed downstream win, not on symmetry.

### Known-value facts

A direct reuse of the branch condition, or a structurally identical repeated
integer comparison, is bound to the selected branch result. Ordinary
`SimplifyCfg`/DCE then removes the redundant check and unreachable arm.

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

## Applies to

A repeated branch is the simplest visible case:

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
not know whether `x < 128`, so that edge keeps using the original general block.

Likewise, if unrelated control flow also reaches that block, it stays on the
same general fallback instead of blocking specialization of the profitable
edges.

## Safety boundaries still in place

The pass still declines:

- loop headers/back-edge reconvergences;
- address-taken blocks and functions with hidden error/inline-asm control flow;
- blocks whose definitions escape and therefore require an SSA merge after the
  versions;
- joins whose successors contain phis, because each specialized exit would need
  a corresponding incoming value there;
- blocks above the hard code-growth budget.

These are not semantic limitations of BBV; they are the boundary of the current
local SSA rewrite.

## Still planned

- Multi-block regions rather than one reconverged block.
- Explicit post-version SSA merging for values escaping the specialized region
  and successor phis.
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
`IrRangeAnalysis` and SSA APIs; no external implementation code was copied or
translated.

- Chevalier-Boisvert & Feeley, *Simple and Effective Type Check Removal through
  Lazy Basic Block Versioning*, ECOOP 2015 — context-specialized blocks under a
  per-block version limit.
- Melançon, Feeley & Serrano, *Static Basic Block Versioning*, ECOOP 2024 —
  ahead-of-time BBV, bounded versions, and retaining only specializations that
  improve generated code.
- LLVM `LazyValueInfo` — consulted for the analysis interface concept of value
  constraints that are specific to a CFG edge; LLVM is Apache-2.0 WITH
  LLVM-exception.
- LLVM `CloneBasicBlock` / path-cloning utilities — consulted only for CFG/SSA
  cloning mechanics under the same license.
