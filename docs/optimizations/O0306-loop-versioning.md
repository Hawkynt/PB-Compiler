# O0306 — Loop versioning

| | |
|---|---|
| **Status** | 🟨 Partial — guarded bounds-check versioning for canonical signed unit-step loops |
| **Stage** | Mid-end |
| **IR** | ✅ `Ir/Passes/LoopVersioning.cs`, in `IrPassManager.Standard()` after LICM/unswitch and before DCE |
| **Verified by** | `LoopVersioningTests`, `IrPassObservableEquivalenceTests` |
| **Related** | [O0304](O0304-guarded-specialization.md), [O0152](O0152-vector-alias-versioning.md), [O0130](O0130-trip-count-versioning.md), [O0117](O0117-bounds-check-merging.md) |

## The idea

Keep the fully general loop, and generate a second one whose expensive per-iteration
checks can be removed when one preheader guard proves the assumptions for the complete
iteration range.

That is the standard loop-versioning shape: a runtime test selects either the original
conservative loop or a cloned loop specialized under stronger facts. LLVM's
`LoopVersioningLICM` describes the same sequence — build a runtime check, clone the
loop, specialize the clone, and branch to one version or the other — while GCC's
vectorizer documentation describes runtime alignment/dependence tests selecting
optimized and unoptimized loop versions.

## Implemented IR slice

The first O0306 slice versions `$ERROR BOUNDS ON` checks emitted by
`IrLowering.RaiseWhen` for a narrow shape that can be proved exactly:

- signed integer `FOR` loops with inclusive `<=` / `>=` tests,
- unit steps (`+1` or `-1`),
- one structural loop exit and no external entry into the body,
- bounds checks whose subscript is the induction variable itself or a direct signed
  widening of it,
- invariant lower/upper bounds,
- a bounded duplication budget.

For every qualifying Error 9 check, the pass derives the endpoint that represents the
minimum or maximum subscript over the whole loop. All endpoint predicates are combined
into one preheader guard. An additional no-wrap predicate excludes inclusive loops that
would have to increment past the signed maximum/minimum before terminating.

The original loop body and its Error 9 checks remain the fallback. `IrCloner` creates a
second loop, and only the cloned bounds branches are specialized to false. Existing exit
phis receive the cloned incoming values, while direct SSA values escaping the loop are
joined in the exit so the rewrite remains valid SSA.

The pass composes with the earlier range fold rather than requiring pristine lowering:
an Error 9 guard already proven false is ignored as pending CFG-cleanup residue while
other dynamic guards in the same loop can still be versioned. Conversely, if literal
endpoints prove that the combined fast-path guard can never succeed, the pass declines
before cloning; otherwise `simplifycfg` could remove the dead clone and expose the same
fallback loop for pointless re-versioning on every fixpoint sweep.

The pass runs after LICM so invariant operands are exposed. It runs after loop unswitching
so the fast clone's deliberately constant-false bounds branches are not mistaken for
loop-invariant branches worth splitting. Later CFG simplification removes the now-dead
trap side of the fast loop.

## Example

```basic
$ERROR BOUNDS ON
SUB Scale(BYVAL n%)
  DIM a%(0 TO 31), i%
  FOR i% = 0 TO n%
    a%(i%) = i%
  NEXT
END SUB
```

Conceptually becomes:

```text
if 0 <= 0 and n <= 31 and n < INTEGER_MAX then
  fast loop      // cloned Error 9 branches are false
else
  checked loop   // original loop and checks
end if
```

Zero-trip cases are allowed to choose the fallback conservatively; the optimization is
not required to make the fast guard minimal, only sufficient. A guard that is already
provably false from literals is not versioned at all.

## Safety boundaries

The matcher deliberately declines rather than extrapolates when it sees:

- affine subscripts such as `a(i + 1)`,
- non-unit or runtime steps,
- unsigned or floating induction variables,
- multiple exits / `EXIT FOR`, external body entries, or unsupported CFG shapes,
- a generated Error 9 guard it cannot express as endpoint bounds,
- armed error handlers or inline assembly.

A source `IF ... THEN ERROR 9` is not treated as a compiler bounds check: the matcher
requires the exact `RaiseWhen` trap shape and rejects source-condition comparisons.

## Remaining O0306 scope

The broader optimization is intentionally still partial. Future slices can add:

- alias/versioning predicates ([O0152](O0152-vector-alias-versioning.md)),
- alignment predicates,
- speculative overflow predicates using [O0219](O0219-overflow-check-elimination.md),
- affine subscript endpoint proofs,
- non-unit and runtime-step induction analysis,
- cost modelling for larger/multiple versions.

Those should feed the same combined guard rather than creating independent nested
versionings.

## References and licensing

- LLVM `LoopVersioningLICM` / loop-versioning utilities — Apache-2.0 WITH LLVM-exception;
  used as architectural/reference material for the guarded original+clone shape.
- GCC tree vectorizer loop-versioning documentation — GPL; used only as a behavioural
  oracle/reference for the runtime-test + conservative-fallback model.

No external implementation code was copied or translated; the pass is an independent
implementation over PB-Compiler's IR and existing `IrCloner`/SSA conventions.
