# O0161 — Function summaries (mod/ref, escape, termination)

| | |
|---|---|
| **Status** | ⬜ Planned (purity is already a whole-call-graph fixpoint — [O0025](O0025-pure-function-folding.md)) |
| **Stage** | Whole-program analysis infrastructure |
| **IR** | 🟡 `Ir/Passes/FunctionSummaries.cs` — the mod/ref half: two bits per procedure, reads and writes, as a fixpoint over the call graph. It starts from PURE and only ever adds impurity, which is what makes a recursive pure function come out pure - starting from impure would need a proof about the cycle before entering it. Anything it cannot see through is maximally impure: an external declaration, an indirect call, an armed error handler, inline asm. `RemoveDeadPureCalls` is the first consumer and is deliberately NOT in the standard pipeline - see the note there and in ROADMAP. Verified by `FunctionSummariesTests` |
| **Related** | [O0025](O0025-pure-function-folding.md), [O0016](O0016-value-fact-analysis.md), [O0159](O0159-return-value-propagation.md), [O0171](O0171-alias-analysis.md) |
| **Split into** | [O0260](O0260-escape-analysis.md), [O0261](O0261-termination-analysis.md) |

## The idea

Record, per procedure, a small summary that every other pass can consult:

| Fact | Used by |
|---|---|
| purity / side effects | [O0025](O0025-pure-function-folding.md), CSE, LICM |
| **mod/ref**: which memory it may read or write | [O0016](O0016-value-fact-analysis.md), [O0060](O0060-memory-ssa.md), [O0140](O0140-load-store-motion.md) |
| which arguments it dereferences or writes through | dead-store, aliasing |
| escape behavior of its parameters | [O0059](O0059-scalar-replacement.md) |
| return range / known bits / constant | [O0159](O0159-return-value-propagation.md) |
| termination | loop transforms, idiom replacement |

The single most valuable entry is **mod/ref**. Today a call invalidates
everything a callee "can reach" by a conservative structural rule
([O0016](O0016-value-fact-analysis.md)); with a real summary it invalidates only
what the callee actually touches.

## Applies to

```basic
FUNCTION Twice%(BYVAL v%)          ' reads nothing, writes nothing
  Twice% = v% * 2
END FUNCTION

DIM x%, y%, a%(0 TO 9)
x% = a%(3)
y% = Twice%(x%)
PRINT a%(3)                        ' the cached load is still valid — but is dropped
```

## Today

The call clears the CSE cache and the value lattice for everything it might
reach, so `a%(3)` is re-read.

## Planned

`Twice%`'s summary says "no memory effects", the cached load survives, and the
lattice keeps its facts across the call.

## What it needs

- A bottom-up walk of the call graph with a fixpoint for recursion — the same
  shape `ClassifyPure` already implements, extended from one bit to a record.
- A conservative default for anything unknown (an external unit, an indirect
  call, inline asm), so soundness is preserved by construction.
