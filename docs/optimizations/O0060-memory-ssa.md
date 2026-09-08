# O0060 — Memory SSA / alias analysis

| | |
|---|---|
| **Status** | ✅ Implemented — function-local Memory SSA plus width-aware alias/clobber walking |
| **Stage** | Mid-end infrastructure |
| **Source** | `Ir/Analysis/IrMemorySsa.cs`, `Ir/Analysis/IrAliasAnalysis.cs` |
| **Consumers** | `Ir/Passes/Gvn.cs` (cross-block redundant-load elimination) |
| **Verified by** | `MemorySsaTests`, `GvnTests` |
| **Related** | [O0046](O0046-ir-gvn.md), [O0049](O0049-ir-licm.md), [O0028](O0028-loop-invariant-code-motion.md), [O0065](O0065-dead-frame-store-elimination.md), [O0171](O0171-alias-analysis.md) |

## What it is

Loads and stores now have a function-local **memory SSA** graph beside the ordinary
value SSA. It follows the standard single-partition shape:

- `IrMemoryLiveOnEntry` is the initial memory version;
- every store and opaque memory barrier creates an `IrMemoryDef`;
- every load is an `IrMemoryUse`;
- control-flow joins get `IrMemoryPhi` nodes at iterated dominance frontiers.

The graph itself versions the whole memory state. Precision comes from its clobber
walker: when a query walks backwards through a store, the shared width-aware
[O0171 alias analysis](O0171-alias-analysis.md) decides whether that store can touch
the queried byte range. Proven-disjoint stores are skipped. Calls and inline
assembly remain conservative barriers.

That split is intentional. Memory SSA describes **ordering**; alias analysis
answers **which bytes may overlap**. Keeping the two separate means later alias
improvements immediately benefit every MemorySSA client without rebuilding a
second provenance model inside the graph.

## Applies to

```basic
DIM a%(0 TO 99), i%, k%, s%
s% = a%(k%)
FOR i% = 0 TO 99
  a%(i%) = i%
  PRINT a%(k%)
NEXT
```

Whether the later read can reuse an earlier one is no longer answered by the crude
question “was there any store?”. The walker asks whether the intervening store may
clobber the exact load location. When the address/range proof says no, both loads
have the same clobbering memory version and GVN may reuse the dominating value.

## Load GVN

[O0046](O0046-ir-gvn.md) now includes a load's clobbering memory version in its
value-numbering key. Two loads are congruent only when all three match:

1. value type,
2. pointer SSA value,
3. alias-aware clobbering memory version.

The dominator-scoped GVN table supplies the other half of the proof: the retained
load must dominate the removed one. A may-alias store or opaque call therefore
changes the key and blocks the fold; a proven non-aliasing store does not.

## CFG merges and loops

Memory phis are placed with the IR's existing dominance-frontier machinery. The
clobber walker recursively resolves their incoming versions. If every path reaches
the same real clobber, the phi is transparent for that query. If paths disagree,
the phi itself is the clobber.

That matters for loops. A back-edge store to a proven-disjoint object still creates
a memory phi structurally, but a load of another object can walk through that
recurrence to its pre-loop clobber. A loop-carried may-alias store keeps the phi and
therefore prevents an unsound reuse.

## Conservative barriers

PB has unusually explicit alias escape points, but the IR still treats anything it
cannot prove as dangerous. In particular:

- calls are memory definitions unless a future mod/ref summary says otherwise;
- inline assembly is an opaque definition (optimized functions containing it are
  already skipped wholesale by the pass manager);
- BYREF/loaded/dynamic pointers remain `MayAlias` unless [O0171](O0171-alias-analysis.md)
  can prove more;
- unknown-width pointer accesses are not guessed.

No new dependency is required; construction reuses `IrDominators` and
`IrAliasAnalysis`.

## Still separate work

Memory SSA supplies the memory-dependence proof, not every legality proof a
transformation needs.

- **LICM of loads** is still disabled. Hoisting a load to a preheader can execute it
  on a zero-trip path where the original program never loaded at all, so the pass
  additionally needs a non-trapping / guaranteed-execution proof. Memory SSA alone
  does not make that safe.
- **Cross-block dead-store elimination** and [O0065](O0065-dead-frame-store-elimination.md)
  can now consume the graph, but remain separate transformations rather than being
  hidden inside this analysis.

## Reference model

The design follows the public MemorySSA model used by LLVM: one function-local
memory partition, `MemoryDef` / `MemoryUse` / `MemoryPhi`, and a walker that skips
non-clobbering definitions. The implementation here is independent C# built on
PB-Compiler's own dominator and alias infrastructure; no external implementation
code or dependency is included.
