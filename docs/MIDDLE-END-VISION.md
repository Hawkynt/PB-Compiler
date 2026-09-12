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

The immediate architectural defect is smaller and more actionable: analyses are mostly called directly by passes. A pass that needs dominance commonly calls `IrDominators.Build(fn)` itself. The pass manager knows only a delegate and an integer change count, so it cannot cache an analysis, know whether a transform preserved it, or invalidate only the facts that became stale.

That is the first refactoring target.

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

Unchanged passes preserve everything. A legacy pass that changes IR conservatively preserves nothing. A transform that only replaces SSA operands can, for example, preserve CFG-only analyses such as dominance. This lets migration happen pass by pass without weakening correctness.

Eventually preservation should support analysis sets such as "all CFG analyses" and transitive invalidation of dependent analyses. The initial implementation intentionally starts with exact analysis keys because it is easy to reason about and hard to make unsound.

## Effects and semantics

Optimization correctness needs an explicit semantics database rather than name-based folklore. Operations and calls must eventually expose facts such as:

```text
Pure
ReadsMemory / WritesMemory
ReadsGlobal / WritesGlobal
MayAllocate
MayThrow / MayTrap
MayBlock / MaySynchronize
PerformsIO
Volatile / Atomic
```

PB's existing runtime semantics make this especially important: a routine that looks like a read may still consume or release a string handle. Effects must describe what the operation actually means, not what its spelling suggests.

The IR also needs deliberate contracts for overflow, floating-point corner cases, invalid shifts/division, pointer/object identity, volatile/atomic behavior, exceptions and observable runtime state. Do not inherit LLVM's poison/undef model accidentally merely because the textual IR resembles LLVM.

## Unified facts

Range analysis is only one abstract domain. The long-term query should look conceptually like `Facts(value, programPoint)`, with branch-local refinement and reusable lattices for constants, integer ranges, known bits, alignment, nullness, dynamic type sets, pointer bases and object identity.

This does not mean one giant mutable `ValueFacts` object. Independent domains should remain independently computable and invalidatable, with a common query/fixed-point framework so passes cooperate instead of duplicating propagation engines.

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

- Introduce lazy function-analysis caching and explicit preservation/invalidation without changing optimization behavior.
- Migrate existing passes incrementally from private analysis reconstruction to the shared analysis manager.
- Split `IrLowering` conceptually into Bound AST -> HIR and HIR -> MIR/SSA stages before adding more source-semantic lowering to the existing monolith.
- Define effect identities for IR operations and runtime/library calls; make memory and call optimizations consume them.
- Define verifier contracts at every new representation boundary.
- Keep every legacy pass conservatively correct during migration; unknown preservation means invalidate.

### Should

- Add post-dominators, an explicit loop forest and shared scalar-evolution infrastructure.
- Turn range/known-bit/alignment/null/type information into reusable abstract domains with branch-local refinement.
- Make MemorySSA, alias/mod-ref and escape analysis share explicit memory/effect semantics.
- Add module/call-graph analysis managers and whole-program reachability as first-class cached analyses.
- Separate target-independent legality from target profitability through cost-model interfaces.
- Move the huge standard pipeline into named, testable phase/fixed-point groups while retaining the proven relative ordering where required.

### Could

- Use bounded e-graphs for pure local expression domains where greedy canonicalization causes phase-order problems.
- Generate or verify algebraic rewrite rules offline with SMT tooling while keeping production compilation dependency-free.
- Add a small-region superoptimizer after the semantics and cost-model contracts are strong enough to prove candidates.
- Add translation validation for risky transforms and profile/autotuning infrastructure for profitability choices.

### Won't in the bootstrap refactor

- Rewrite every pass or every IR layer at once.
- Change pass order while introducing the pass/analysis contract.
- Add external optimizer dependencies.
- Copy LLVM/MLIR implementation code or adopt LLVM semantics accidentally.
- Replace target-independent operations with target-shaped arithmetic merely to make one backend easier to write.

## Bootstrap slice in this PR

This PR establishes the first mechanism rather than pretending the migration is already complete:

1. `IrAnalysisManager` lazily computes and caches typed function analyses.
2. `IrPreservedAnalyses` makes invalidation an explicit pass result instead of an undocumented side effect.
3. `IrPassResult` separates "did this transform change IR?" from "which knowledge is still valid?".
4. `IrFunctionPassPipeline` provides the analysis-aware execution core while legacy delegates can be adapted conservatively.
5. Correlated value propagation is the first migrated real pass. It consumes cached dominators and explicitly preserves them because it only rewrites operands and does not alter the CFG.

`IrPassManager.Standard` remains the production pipeline in this slice. The next mechanical step is to make it delegate function-pass execution to the new core, then migrate passes one by one. Keeping that wiring separate avoids coupling a new invalidation model to a wholesale edit of the carefully ordered production pipeline.

## Reference architecture

The design uses public architectural contracts from established compiler infrastructure as reference material, not implementation source:

- LLVM New Pass Manager: <https://llvm.org/docs/NewPassManager.html> — lazy analysis managers, cached results, preserved-analysis invalidation and scoped IR-unit managers.
- MLIR Pass Infrastructure: <https://mlir.llvm.org/docs/PassManagement/> — cached analyses, explicit preservation and structured pass pipelines.
- LLVM MemorySSA: <https://llvm.org/docs/MemorySSA.html> — SSA-style memory versions and def/use reasoning.
- LLVM Language Reference / UB manual: <https://llvm.org/docs/LangRef.html> and <https://llvm.org/docs/UndefinedBehavior.html> — useful evidence for why poison/undef/trap behavior must be deliberate rather than implicit.

LLVM is Apache-2.0 WITH LLVM-exception. No LLVM/MLIR implementation code is copied or translated here; the PB-Compiler implementation is original and uses only the architectural behavior described by the documentation.
