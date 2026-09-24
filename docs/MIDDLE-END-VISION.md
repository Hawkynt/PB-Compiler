# Middle-end architecture vision

The compiler middle-end is not a bag of optimization passes. It is a knowledge-refinement system: preserve source semantics, expose facts, remove syntactic accidents, specialize when justified, and delay irreversible lowering until the optimizer has exhausted the information that a richer representation carries.

A literally optimal compiler is impossible in the general case. The engineering target is therefore more useful: make optimization quality depend on analysis precision, profitability decisions and compile-time budget instead of representation mistakes or pass-manager limitations.

## Core principles

1. **Preserve semantics.** A lowering step may remove syntax, but it must not silently remove facts a later analysis could exploit.
2. **Expose facts.** Analyses are shared compiler infrastructure, not private helpers recreated inside individual passes.
3. **Canonicalize aggressively.** Equivalent computations should converge toward equivalent IR as soon as doing so is semantically legal.
4. **Delay irreversible lowering.** Keep arrays, calls, guards, object identity, runtime effects and other semantic operations explicit until the last layer that can profit from them.
5. **Separate legality from profitability.** `CanTransform` and `ShouldTransform` answer different questions.
6. **Iterate deliberately.** Cheap cooperating transformations run in bounded fixed-point groups instead of relying on one fortunate pass order.
7. **Let the target decide representation.** Target-independent IR states computation; the target cost model chooses instructions, vector width, unroll factors and layout.
8. **Verify transformations.** Structural verification is mandatory; differential tests and eventually translation validation cover semantic preservation.

## Intended representation stack

```text
Source
  -> Syntax AST
  -> Bound AST
  -> HIR
  -> Canonical HIR
  -> MIR
  -> SSA
  -> Optimized SSA
  -> Low IR
  -> Machine SSA
  -> Allocated Machine IR
  -> Machine code
```

These are conceptual boundaries, not a requirement for eleven assemblies or eleven unrelated object models. A representation earns a separate layer only when it has a distinct semantic contract and consumers that benefit from that contract.

| Layer | Contract |
|---|---|
| Syntax AST | What the programmer wrote: source locations, recovery and surface syntax. No optimization. |
| Bound AST | Resolved symbols, types, conversions, overloads, value categories and language rules. The optimizer never repeats binding. |
| HIR | Desugared language semantics while retaining high-level operations and identities useful to optimization. |
| Canonical HIR | Equivalent source forms converge; implicit behavior becomes explicit without destroying useful semantic operations. |
| MIR | Explicit evaluation order, control flow, side effects and exceptional edges. |
| SSA | Typed values, CFG, phi nodes, explicit memory operations and def-use chains. Primary mathematical optimization form. |
| Low IR | Target-independent legalization after high-level checks and abstractions have had a chance to disappear. |
| Machine SSA | Selected target operations with virtual registers; still suitable for machine-level CSE, folding and scheduling. |
| Machine IR | Physical registers, stack slots, final scheduling and layout constraints. |

### Where PB-Compiler is today

The repository already owns much of the hard machinery: typed SSA values, exact use-lists, CFG verification, dominators/frontiers, MemorySSA, alias analysis, range analysis, dependence analysis, SCCP, GVN, SROA, interprocedural specialization, WPD, PGO-driven transforms, equality saturation and native machine lowering.

The current `PowerBasic.Compiler.Ir` layer therefore spans several boxes in the table above. `IrLowering` lowers the Bound AST directly into a representation that contains source-semantic lowering decisions, MIR-like explicit control flow and SSA/Low-IR operations. That was a sensible bootstrap path; it should now be separated by contracts before more optimization knowledge is added.

The first architectural defect being removed is analysis ownership. Historically a pass that needed dominance called `IrDominators.Build(fn)` itself, while `IrPassManager` knew only a delegate and an integer change count. That prevented safe analysis caching and precise invalidation. The production function-pass path now has a shared analysis contract; migration can proceed without changing the proven pass order.

## Analysis backbone

The reusable knowledge system should converge on these function/module/loop analyses:

```text
CFG / reachability
SSA + def-use
Dominators / post-dominators / dominance frontiers
Loop forest + induction variables + scalar evolution
MemorySSA
Alias + mod/ref
Escape / object identity
Effects
Range / symbolic inequalities
Known bits / congruences / alignment
Type sets / nullness
Call graph / whole-program reachability
Dependence information
Profile information
Target cost information
```

Most transformations should consume these facts rather than independently rediscovering them. Analyses may depend on other analyses. Results are lazy and cached for an IR unit until a transform invalidates them.

A transformation reports both what changed and what remains valid:

```text
pass(function, analyses) -> { changes, preserved analyses }
```

Unchanged passes preserve everything. A legacy pass that changes IR conservatively preserves nothing. A transform that only changes values or moves instructions without changing block topology can preserve the named CFG analysis set rather than enumerating dominators, post-dominators and loop facts one by one.

Analysis-to-analysis queries are dependencies, not implementation details. If MemorySSA is derived from dominators and a transform invalidates dominance, preserving only MemorySSA is not sufficient: the analysis manager invalidates the dependent result transitively. Dependencies are recorded dynamically while analyses are computed.

Named preservation sets are now part of the contract. `IrAnalysisSets.Cfg` is the first: dominators, post-dominators and the natural-loop forest declare membership once, and CFG-preserving transforms preserve the set. Dependency invalidation still wins over set membership, so preserving a set cannot keep a result whose non-preserved prerequisite became stale.

## Effects and semantics

Optimization correctness needs an explicit semantics database rather than name-based folklore. Operations and calls should expose facts such as:

```text
ReadsMemory / WritesMemory
MayAllocate / MayRelease
MayThrow / MayTrap
MayBlock / MaySynchronize
PerformsIO
Volatile / Atomic
Deterministic
```

The first explicit vocabulary now exists as `IrEffectKind`, `IrEffectSummary` and `IrEffects`. The checked LLVM floating math intrinsics are deterministic, effect-free and speculatable; every unmodeled runtime/library call remains maximally conservative. The table now also records PB-specific ownership/mod-ref contracts for borrowed versus consuming string length, string duplication/free, core owned-string construction/concatenation/comparison/slicing producers, raw memory compare/copy/set, dynamic-array allocation/reallocation/free, HUGE/EMS allocation-release-zeroing/mapping/query operations, and runtime-error transfer. Memory intrinsics refine volatility from their call-site flag instead of treating a literal non-volatile copy as a volatile barrier.

That contract now also classifies every IR instruction explicitly. DCE and dead-loop elimination use discardability, LICM/GVN use speculation/CSE properties, MemorySSA derives uses and definitions from memory effects, dependence analysis asks about memory participation, and function summaries propagate the same operation facts through the call graph. There is deliberately no default instruction case: introducing a new IR operation without defining its effects fails closed instead of silently inheriting purity or opacity.

PB's runtime semantics make conservatism essential: a routine that looks like a read may still consume or release a string handle. `rt_str_len` is explicitly a read plus release, while `rt_str_len_borrow` is a deterministic read without a lifetime change. `rt_str_dup` and `rt_str_const` allocate fresh owned handles and may fail; concatenation/slicing/repeat operations both consume owned inputs and allocate results; string comparison consumes its owned operands even when its numeric result is dead. Paged-array runtime calls distinguish allocation/release, zeroing/mapping writes, the trap-capable EMS page-frame cache, and the read-only non-deterministic `FRE(-11)` query. Printing and all still-unmodeled externals remain opaque until their contracts are established deliberately.

The IR still needs fuller contracts for overflow, floating-point corner cases, invalid shifts/division, pointer/object identity, volatile/atomic behavior, exceptions and observable runtime state. Do not inherit LLVM's poison/undef model accidentally merely because the textual IR resembles LLVM.

## Unified facts

Range analysis is only one abstract domain. The long-term query should look conceptually like `Facts(value, programPoint)`, with branch-local refinement and reusable lattices for constants, integer ranges, known bits, alignment, nullness, dynamic type sets, pointer bases and object identity.

This does not mean one giant mutable `ValueFacts` object. Independent domains remain independently computable and invalidatable, with common query/fixed-point infrastructure so passes cooperate instead of duplicating propagation engines.

The current shared domains include branch-refined integer ranges, FP domains adapted from those ranges, bounded known-zero/known-one bits, explicit-guard pointer nullness, and low-bit pointer alignment. `IrValueFacts` is the common program-point query facade over those independently cached domains; it owns no second lattice, and analysis dependency invalidation prevents the facade from surviving a stale prerequisite. Alignment facts combine dominating canonical guards with explicit pointer round-up arithmetic, but remain target-neutral facts about the IR pointer/integer representation rather than promises about a machine load/store. `DemandedBits` consumes known bits directly where that domain-specific API is sufficient; pointer-check elimination consumes the combined facade, and basic-block versioning now asks the shared alignment domain for speculative path implications instead of carrying its own mask parser.

## Guards and speculation

Runtime assumptions should become first-class semantic objects before target lowering. A guarded specialization can state facts such as:

```text
type(x) == T
p does not alias q
alignment(p) >= 32
length >= 16
```

The optimized path consumes the facts; the failed guard reaches a generic fallback. This is the common mechanism behind loop versioning, speculative devirtualization, alignment specialization and value-profile specialization.

## Pass pipeline

The pipeline should become structured rather than one monolithic ordered list:

```text
canonical HIR fixed point
  -> MIR formation
  -> SSA construction
  -> scalar simplification fixed point
  -> memory/object optimization fixed point
  -> loop optimization groups
  -> interprocedural fixed point
  -> late scalar cleanup
  -> target-independent lowering
  -> machine SSA optimization
  -> allocation/scheduling/layout
```

Pass order still matters inside a group, but dependencies should be stated through required analyses and representation contracts rather than encoded only in comments. The production plan now assigns every transform to an inspectable named phase while preserving the historical flattened order. Function optimization still uses the proven whole-pipeline bounded fixed point during this migration; exhausting that budget now emits an explicit diagnostic naming the transforms that continued to change the final sweep. Per-phase fixed points can therefore be introduced later as deliberate scheduling changes rather than accidentally as part of the structural refactor.

## Migration plan

### Must

- Continue migrating existing passes from private analysis reconstruction to the shared analysis manager.
- Continue the incremental Bound AST -> HIR -> MIR/SSA split. Arrays and direct user calls are now concrete HIR families: array HIR carries resolved storage/bounds/lifetime semantics, while direct-call HIR carries the resolved procedure, parameter-order/default expansion, parameter type, BYVAL/BYREF, result identity and calling convention. SSA lowering consumes those contracts instead of rereading binder side tables. Delegates/indirect calls remain on their separate explicit path. Move additional source-semantic families across the same boundary rather than growing the monolithic direct path.
- Expand effect identities from the first external-call contracts to IR operations and precisely modeled runtime/library calls.
- Extend the verifier contracts as new HIR/MIR boundaries are introduced. Optimized SSA and Low IR are now verifier-backed, and machine stages are represented by the machine-side product rather than source-module relabeling.
- Keep every legacy pass conservatively correct during migration; unknown preservation means invalidate.

### Should

- Extend scalar evolution beyond bootstrap additive recurrences and exact canonical trip counts where consumers justify the extra lattice complexity.
- Grow the reusable fact domains beyond range/known-bits/nullness/alignment and the new pointer-base/object-identity/escape slices. Exact function-value target sets are now shared module facts; general object dynamic type sets are still missing. Extend pointer identity beyond GEP/bitcast/select roots only when a consumer can justify the extra proof complexity, and keep BYREF/load/far-pointer cases conservative.
- Continue unifying alias/mod-ref and escape/object-identity queries. MemorySSA consumes shared `IrModRefAnalysis` and `IrPointerIdentityAnalysis`; DSE and redundant-memory now consume the same identity facts, and O0065 uses shared `IrPointerEscapeAnalysis` instead of a private pointer-use walk. Richer interprocedural object identity/escape still needs one contract. Function mutations conservatively invalidate module facts until cross-unit preservation becomes explicit.
- Continue migrating module transforms onto the module analysis manager. The direct call graph, function summaries, conservative whole-program reachability and exact function-target sets are first-class cached analyses with dependency invalidation; IPCP, dead-pure-call elimination, aggregate-parameter reduction, return-structure reduction, profile-guided indirect-call promotion, speculative/whole-program devirtualization and context-sensitive cloning now consume them. Remaining interprocedural transforms still need analysis-aware entries and precise preservation contracts.
- Continue separating target-independent legality from target profitability through cost-model interfaces. Arithmetic reciprocal reuse and indirect-call promotion now have explicit middle-end cost interfaces; remaining hard-coded code-growth/threshold policies should migrate only when their legality boundary is clear.
- Move the huge standard pipeline into named, testable phase/fixed-point groups while retaining the proven relative ordering where required.

### Could

- Use bounded e-graphs for pure local expression domains where greedy canonicalization causes phase-order problems.
- Generate or verify algebraic rewrite rules offline with SMT tooling while keeping production compilation dependency-free.
- Add a small-region superoptimizer after the semantics and cost-model contracts are strong enough to prove candidates.
- Add translation validation for risky transforms and profile/autotuning infrastructure for profitability choices.

### Won't in this refactor

- Rewrite every pass or every IR layer at once.
- Change pass order merely while introducing analysis infrastructure.
- Add external optimizer dependencies.
- Copy LLVM/MLIR implementation code or adopt LLVM semantics accidentally.
- Replace target-independent operations with target-shaped arithmetic merely to make one backend easier to write.

## Current status in this PR

The PR now has a production analysis substrate rather than only a sketch:

1. `IrAnalysisManager` lazily computes typed function analyses and dynamically records analysis-to-analysis dependencies.
2. `IrPreservedAnalyses` / `IrPassResult` carry exact preservation and named preservation sets; stale prerequisites invalidate dependents transitively.
3. `IrFunctionPassPipeline` is the function-pass execution core behind `IrPassManager`; legacy delegates remain conservative.
4. Shared CFG analyses include dominators/frontiers, post-dominators/frontiers and an explicit natural-loop forest; module passes execute through a shared module analysis manager.
5. Shared value/memory analyses include MemorySSA, mod/ref, pointer-base/object identity, pointer escape, branch-refined integer ranges, FP domains, bootstrap scalar evolution and known bits. Module analyses now include a cached direct call graph, function summaries, conservative whole-program reachability and complete/incomplete function-value target sets. MemorySSA can refine internal-call memory behavior from those module summaries without weakening standalone conservatism.
6. Scalar evolution exposes additive `{start,+,step}` recurrences and bounded exact trip-count proofs using fixed-width integer semantics; `CountedLoop` and migrated loop consumers reuse those facts.
7. Migrated transforms include correlation, pointer-check elimination, GVN, LICM, integer/FP range folding, reciprocal loop reasoning and IV simplification. CFG-preserving transforms preserve `IrAnalysisSets.Cfg` rather than manually maintaining a key list.
8. `DemandedBits` consumes the known-bits domain; DCE, dead-loop elimination, GVN, LICM, MemorySSA, dependence analysis and function summaries consume the central IR-operation/effect contract, including the first precise PB runtime ownership/mod-ref rows.
9. Verification deliberately remains independent of the analysis cache: `VerifyEachPass` must catch an incorrect preservation claim instead of trusting it.
10. The standard pipeline is now partitioned into named, inspectable phases without changing its flattened pass order; bounded function fixed points report the transforms responsible when they fail to converge.
11. Interprocedural constant propagation, dead-pure-call elimination, aggregate-parameter reduction, return-structure reduction, profile-guided indirect-call promotion, speculative/whole-program devirtualization and context-sensitive cloning now consume shared cached module analyses instead of privately rebuilding call-graph facts. Exact singleton target proofs bypass both profile and static guarded devirtualization and are left for O0279's direct rewrite; cloning invalidates/rebuilds caller graphs between source functions and preserves the final rebuilt graph. Whole-program devirtualization's complete target-set solver is itself a cached module analysis rather than pass-private state. Aggregate-parameter reduction and devirtualization invalidate/rebuild the graph between rewrites inside their own fixed points; O0281 preserves call graph/reachability while invalidating function summaries after store removal.
12. Array operations and statically bound user calls now cross concrete Bound AST -> HIR -> SSA families. Arrays retain identity, storage class, bounds/lifetime and Error 9 policy; direct SUB/FUNCTION calls retain resolved procedure identity, parameter-order/default expansion, BYVAL/BYREF, parameter/result types and calling convention before ABI-shaped argument lowering. `IrLowering` no longer reads `CallBindings`/`ReorderedArguments` while forming direct SSA calls.
13. `IrValueFacts` now composes branch-refined ranges, known bits, explicit pointer nullness and pointer alignment behind one program-point query surface; `PointerCheckElim` consumes it instead of carrying a private dominator/nullness solver.
14. Basic-block versioning now runs as an analysis-aware pass and consumes shared alignment assumptions rather than reconstructing pointer-mask facts privately.
15. O0271 indirect-call promotion now separates legality from profitability through `IIrCallCostModel`; native `TargetCost` and targetless historical policy feed the same transform without changing its established 30% default threshold.
16. No new external dependency has been introduced and the standard pass order has not been reordered.

The target-neutral representation boundaries now carry verifier contracts rather than acting as progress labels. `OptimizedSsa` requires structurally valid SSA; `LowIr` additionally requires an explicit semantic/effect contract for every operation that crosses the boundary. Unknown external calls remain legal through their conservative effect contract, while a new unclassified IR instruction cannot silently reach a backend. Machine SSA/IR are distinct machine-side products: lowering leaves the source `IrModule` at `LowIr` instead of relabeling it after selection/allocation.

The next representation work is the high-level side of the split: continue extending the Bound AST -> HIR boundary beyond arrays and direct calls to source-semantic operations that currently lose useful identity inside `IrLowering`, then lower HIR -> MIR/SSA through an explicit contract. Production policy is already centralized in `IrMiddleEndPipeline`, so that split can proceed without reintroducing caller-owned optimizer schedules.

## Reference architecture

The design uses public architectural contracts from established compiler infrastructure as reference material, not implementation source:

- LLVM New Pass Manager: <https://llvm.org/docs/NewPassManager.html> — lazy analysis managers, cached results, preserved-analysis sets/invalidation and scoped IR-unit managers.
- MLIR Pass Infrastructure: <https://mlir.llvm.org/docs/PassManagement/> — cached analyses, explicit preservation and structured pass pipelines.
- LLVM Loop Terminology: <https://llvm.org/docs/LoopTerminology.html> — natural loops, headers, latches, preheaders and canonical loop terminology.
- LLVM ScalarEvolution: <https://llvm.org/doxygen/classllvm_1_1ScalarEvolution.html> — recurrence/trip-count analysis interface.
- LLVM MemorySSA: <https://llvm.org/docs/MemorySSA.html> — SSA-style memory versions and def/use reasoning.
- LLVM ValueTracking / KnownBits: <https://llvm.org/doxygen/ValueTracking_8h_source.html> — known-zero/known-one query model.
- LLVM Language Reference: <https://llvm.org/docs/LangRef.html> — memory effects, `speculatable`, `willreturn`, `nosync` and explicit operation contracts.
- LLVM UB manual: <https://llvm.org/docs/UndefinedBehavior.html> — why poison/undef/trap behavior must be deliberate rather than implicit.

LLVM is Apache-2.0 WITH LLVM-exception. No LLVM/MLIR implementation code was copied or translated here; the PB-Compiler implementation is original and uses only public architectural behavior as reference.
