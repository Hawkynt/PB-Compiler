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

The first explicit vocabulary now exists as `IrEffectKind`, `IrEffectSummary` and `IrEffects`. The initial external-call table intentionally preserves existing behavior: the checked LLVM floating math intrinsics are deterministic, effect-free and speculatable; every unmodeled runtime/library call remains maximally conservative.

That contract now also classifies every IR instruction explicitly. DCE and dead-loop elimination use discardability, LICM/GVN use speculation/CSE properties, MemorySSA derives uses and definitions from memory effects, dependence analysis asks about memory participation, and function summaries propagate the same operation facts through the call graph. There is deliberately no default instruction case: introducing a new IR operation without defining its effects fails closed instead of silently inheriting purity or opacity.

PB's runtime semantics make conservatism essential: a routine that looks like a read may still consume or release a string handle. `rt_str_len`, `rt_str_dup`, printing, memcpy and unknown externals therefore remain outside the effect-free contract until their exact semantics are modeled deliberately.

The IR still needs fuller contracts for overflow, floating-point corner cases, invalid shifts/division, pointer/object identity, volatile/atomic behavior, exceptions and observable runtime state. Do not inherit LLVM's poison/undef model accidentally merely because the textual IR resembles LLVM.

## Unified facts

Range analysis is only one abstract domain. The long-term query should look conceptually like `Facts(value, programPoint)`, with branch-local refinement and reusable lattices for constants, integer ranges, known bits, alignment, nullness, dynamic type sets, pointer bases and object identity.

This does not mean one giant mutable `ValueFacts` object. Independent domains remain independently computable and invalidatable, with common query/fixed-point infrastructure so passes cooperate instead of duplicating propagation engines.

The current shared domains already include branch-refined integer ranges, FP domains adapted from those ranges, and a bounded `IrKnownBitsAnalysis` for known-zero/known-one facts. Known bits covers constants, bitwise logic, integer truncation/extension, selects and conservative phi meets. `DemandedBits` is its first consumer and can now recognize a neutral demanded-bit mask proved by a non-literal SSA expression.

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

Pass order still matters inside a group, but dependencies should be stated through required analyses and representation contracts rather than encoded only in comments. Fixed-point groups should have explicit iteration budgets and diagnostics for non-convergence.

## Migration plan

### Must

- Continue migrating existing passes from private analysis reconstruction to the shared analysis manager.
- Split `IrLowering` conceptually into Bound AST -> HIR and HIR -> MIR/SSA stages before adding more source-semantic lowering to the existing monolith.
- Expand effect identities from the first external-call contracts to IR operations and precisely modeled runtime/library calls.
- Define verifier contracts at every new representation boundary.
- Keep every legacy pass conservatively correct during migration; unknown preservation means invalidate.

### Should

- Extend scalar evolution beyond bootstrap additive recurrences and exact canonical trip counts where consumers justify the extra lattice complexity.
- Grow known-bit/alignment/null/type information as reusable abstract domains with branch-local refinement where useful.
- Make MemorySSA, alias/mod-ref and escape analysis share explicit memory/effect semantics.
- Add module/call-graph analysis managers and whole-program reachability as first-class cached analyses; module transforms must participate in invalidation before cached summaries are introduced.
- Separate target-independent legality from target profitability through cost-model interfaces.
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
5. Shared value/memory analyses include MemorySSA, branch-refined integer ranges, FP domains, bootstrap scalar evolution and known bits.
6. Scalar evolution exposes additive `{start,+,step}` recurrences and bounded exact trip-count proofs using fixed-width integer semantics; `CountedLoop` and migrated loop consumers reuse those facts.
7. Migrated transforms include correlation, pointer-check elimination, GVN, LICM, integer/FP range folding, reciprocal loop reasoning and IV simplification. CFG-preserving transforms preserve `IrAnalysisSets.Cfg` rather than manually maintaining a key list.
8. `DemandedBits` consumes the known-bits domain; DCE, dead-loop elimination, GVN, LICM, MemorySSA, dependence analysis and function summaries consume the central IR-operation/effect contract.
9. Verification deliberately remains independent of the analysis cache: `VerifyEachPass` must catch an incorrect preservation claim instead of trusting it.
10. No new external dependency has been introduced and the standard pass order has not been reordered.

The next architectural boundary is phase naming below the target-neutral middle end. Production policy is centralized in `IrMiddleEndPipeline`, with `RunNativeModule` and `RunHostedModule` owning restart and inlining choreography; `IrPassManager` only executes registered passes and invalidates shared analyses. The remaining representation split (`Bound AST -> HIR -> MIR/SSA`) can proceed without reintroducing caller-owned optimizer schedules.

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
