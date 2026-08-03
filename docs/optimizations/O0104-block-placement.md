# O0104 — Branch probability inference and block placement

| | |
|---|---|
| **Status** | ⬜ Planned (the by-construction shape is done — [O0041](O0041-branch-layout.md)) |
| **Stage** | Emitter / assembler |
| **Related** | [O0041](O0041-branch-layout.md), [O0094](O0094-branch-inversion.md), [O0105](O0105-hot-cold-splitting.md) |

## The idea

[O0041](O0041-branch-layout.md) lays every `IF` and every loop out for the
static predictor by construction. The next step is to *infer* which edges are
likely and lay out the common path as one contiguous fall-through trace:

- a loop back-edge is taken almost always;
- an early `EXIT SUB` guard is usually not taken;
- an error path (`ON ERROR`, a `PRINT "error"` arm, an `END` arm) is cold;
- a comparison against a constant boundary in a loop is usually the continue
  case.

Without profile data these are heuristics — the same ones LLVM ships as
`BranchProbabilityInfo`.

## Applies to

```basic
SUB Work(BYVAL n%)
  IF n% < 0 THEN
    PRINT "bad argument"      ' cold
    EXIT SUB
  END IF
  ...                          ' hot
END SUB
```

## Today

Both arms are laid out in source order, so the cold diagnostic sits in the
middle of the hot instruction stream.

## Planned

The cold arm moves after the procedure's hot body, and the guard becomes a
forward not-taken branch to it — the hot path is one straight run of bytes.

## What it needs

- An edge-probability pass over the CFG the SSA mid-end already builds.
- Block reordering in the assembler, which currently emits in source order;
  jumps must be re-relaxed afterwards ([O0035](O0035-jump-relaxation.md)).
- On an 8086 the payoff is prefetch-queue locality rather than branch
  prediction — which is exactly why the decision belongs to a per-target cost
  model ([O0174](O0174-target-cost-models.md)).
