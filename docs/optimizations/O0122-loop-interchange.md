# O0122 — Loop interchange

| | |
|---|---|
| **Status** | 🟨 Partial — conservative two-level IR interchange implemented; general dependence-direction vectors and richer nests remain |
| **Stage** | Mid-end |
| **Source** | `Ir/Passes/LoopInterchange.cs` |
| **Verified by** | `PowerBasic.Compiler.Tests/Ir/LoopInterchangeTests.cs` |
| **Related** | [O0062](O0062-loop-restructuring.md), [O0110](O0110-general-induction-variables.md), [O0172](O0172-loop-dependence-analysis.md) |

## The idea

Swapping the nesting order of two loops changes a strided traversal into a
contiguous one. PowerBASIC's documented multidimensional-array representation
is **column-major**: the first subscript varies fastest. For a correctly lowered
`a(i, j)`, an innermost `j` therefore strides by the first dimension while an
innermost `i` walks adjacent elements.

```basic
DIM a%(0 TO 99, 0 TO 99), i%, j%
FOR i% = 0 TO 99
  FOR j% = 0 TO 99
    a%(i%, j%) = 1           ' strided in PowerBASIC's column-major layout
  NEXT
NEXT
```

becomes

```basic
FOR j% = 0 TO 99
  FOR i% = 0 TO 99
    a%(i%, j%) = 1           ' first subscript now innermost
  NEXT
NEXT
```

when the dependence facts permit it.

## Implemented IR slice

`LoopInterchange` recognizes a deliberately strict canonical nest:

- two `CountedLoop`s with constant, non-wrapping integer start/limit/step;
- a rectangular iteration space — the inner start/limit/step are constants and
  therefore cannot depend on the outer counter;
- the inner loop is the only body of the outer loop;
- the innermost body is one basic block;
- no calls, inline assembly, integer/floating division/remainder, or other
  instruction whose ordering may expose a trap or side effect outside the
  memory model;
- body-produced SSA values do not escape the nest.

The CFG does **not** need to be cloned. A perfect canonical nest already has the
same block topology after interchange. The pass keeps the blocks and body in
place, creates new induction phis/tests/increments, and swaps which source
counter occupies the outer and inner loop-control positions.

### Memory legality

The pass forms a two-counter affine byte address for each body load/store:

`root + outerStride * outerIteration + innerStride * innerIteration + constant`

Every intermediate integer expression must stay within its signed IR type for
the whole rectangle; a potentially wrapping expression declines.

O0171 is used first for distinct-object disambiguation. Read/read pairs do not
constrain execution order. This first O0122 slice is deliberately stricter than
the full O0172 design for writes:

- if a write may alias a *different* access site, interchange declines until
  nested direction vectors are available;
- a write's own two-dimensional address must be injective over the rectangle;
- the GCD of the two byte strides must be at least the access width, so distinct
  starts cannot partially overlap.

This proves the supported copy/fill/elementwise shapes without treating an
unknown dependence as independence.

### Profitability

Profitability follows the **actual IR address coefficients**, not a hard-coded
array-layout assumption. For every memory access the pass compares the absolute
byte displacement produced by advancing the current inner counter with the
one produced by advancing the current outer counter. Interchange happens only
when the summed proposed inner displacement is strictly smaller.

That makes the optimization target-independent and prevents a second swap on
the next pass-manager fixpoint iteration.

### Post-loop counters

PowerBASIC leaves `FOR` counters live after `NEXT`. The pass preserves that
observable state:

- uses of the old outer counter outside the nest become its proven final
  constant;
- the common mem2reg carrier phi that exposes the final inner counter after the
  outer loop is recognized, replaced by the inner counter's proven final
  constant, and removed.

If a more complicated carried value participates in the loop, this slice
backs off.

## Array layout contract

Both lowering paths now use PowerBASIC's documented **first-subscript-fastest**
physical layout. Static and dynamic multidimensional addresses therefore agree
with the language ABI rather than merely agreeing with each other. The semantic
fix is regression-tested through `VARPTR` byte deltas, two-dimensional
`REDIM PRESERVE`, and the genuine-PBC differential array battery (`DIFF56`).

O0122 still deliberately reads the byte strides present in IR instead of
hard-coding a source-language dimension order. That keeps the transformation
usable for pointer/record access patterns and for future targets whose IR may
contain storage not originating from a BASIC array.

## Still planned

- Extend O0172 from one-level distances to full nested dependence direction
  vectors, then use the standard lexicographic-permutation legality rule.
- Support multiple potentially-aliasing access sites when those direction
  vectors prove the swapped order legal.
- Support larger/multi-block perfect nests and selected imperfect nests.
- Preserve general LCSSA/reduction values rather than declining when a body
  result escapes.
- Combine the legality result with [O0174](O0174-target-cost-models.md) for
  cache-line/vector-width-aware profitability rather than byte stride alone.

## References / design basis

LLVM's LoopInterchange implementation uses the standard rule for a perfect
nest: after permuting dependence-vector columns, no row may have `>` as its
leftmost non-`=` direction. It also separates legality from profitability and
conservatively rejects loop structures it does not understand. This
implementation uses those rules as a behavioral/design reference only; it is
independently structured for PB-Compiler's IR and no LLVM implementation code
or comments are copied.
