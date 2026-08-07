# O0114 — Loop unswitching

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **IR** | ✅ `Ir/Passes/LoopUnswitch.cs` — the loop is cloned once per outcome and the test moves to the preheader. Placed AFTER `Licm` in `IrPassManager.Standard()`, and that ordering IS the composition: `IF mode THEN` inside a loop lowers to a compare computed IN the loop, and a condition defined inside the region cannot be specialized by cloning, because each clone gets its own copy of the compare and binding the original to a constant reaches nothing. LICM hoists it out first. The pass also inserts the exit phis itself - this IR does not keep loop-closed SSA, so a value the loop computes is read directly afterwards rather than through a phi at the boundary, and removing the original loop would leave those operands dominating nothing. Verified by `LoopUnswitchTests` and `IrPassObservableEquivalenceTests` |
| **Related** | [O0028](O0028-loop-invariant-code-motion.md), [O0107](O0107-branch-folding-through-phi.md), [O0130](O0130-trip-count-versioning.md) |

## The idea

A conditional inside a loop whose condition is **loop-invariant** is tested on
every iteration for an answer that never changes. Hoisting it out and cloning
the loop for each outcome tests it once — and, crucially, lets each cloned body
be specialized with the condition's value propagated into it, which usually
deletes half the body.

## Applies to

```basic
DIM i%, mode%, a%(0 TO 999)
FOR i% = 0 TO 999
  IF mode% THEN
    a%(i%) = a%(i%) * 2
  ELSE
    a%(i%) = a%(i%) + 1
  END IF
NEXT
```

## Today

1 000 tests of `mode%`, 1 000 branches, and a body that cannot be vectorized or
scheduled as a straight line.

## Planned

```basic
IF mode% THEN
  FOR i% = 0 TO 999 : a%(i%) = a%(i%) * 2 : NEXT
ELSE
  FOR i% = 0 TO 999 : a%(i%) = a%(i%) + 1 : NEXT
END IF
```

Each loop body is now branch-free straight-line code — which is precisely the
shape [O0026](O0026-auto-vectorization.md) and
[O0030](O0030-induction-variable-strength-reduction.md) require.

## What it needs

- Loop-invariance for the condition, which
  [O0028](O0028-loop-invariant-code-motion.md)'s write-set scan already
  computes.
- A **code-size budget** — the body is duplicated once per outcome, so this is a
  `$OPTIMIZE SPEED` transformation and it must not fire on large bodies.
- Zero-trip safety: the hoisted test must not evaluate anything that could trap
  when the loop would not have run (the same argument LICM uses).
