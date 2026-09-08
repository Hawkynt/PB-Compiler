# O0288 — Allocation sinking

| | |
|---|---|
| **Status** | 🟡 Partial — string-literal allocations used only by one no-else `IF` arm sink with their cleanup; general allocation/CFG sinking remains planned |
| **Stage** | Mid-end |
| **IR** | 🟡 `Ir/Passes/AllocationSinking.cs` — registered as `allocsink` in `IrPassManager.Standard()` |
| **Related** | [O0253](O0253-store-sinking.md), [O0286](O0286-allocation-elimination.md), [O0105](O0105-hot-cold-splitting.md) |

## The idea

An allocation performed unconditionally but used only on a rare path should
happen **on that path**. The common execution then pays nothing.

## Applies to

```basic
DIM msg$, err%
msg$ = "operation failed: code "     ' allocated every call
IF err% THEN PRINT msg$; err%        ' used almost never
```

## Implemented slice

`AllocationSinking` recognizes the IR shape produced by that no-else `IF` after
`mem2reg`: an `rt_str_const` result is read only in one immediate successor, the
successor is entered only from the allocating block, and its owned handle is
freed immediately at the join. The allocator and that matching `rt_str_free`
move into the arm together.

The pass deliberately refuses to cross calls, loads, stores, trapping division
or arbitrary control flow. The one call it may cross is `rt_str_free(null)`,
because the string runtime defines freeing the zero handle as a no-op; this is
the bookkeeping emitted by the first assignment to a zero-initialized BASIC
string variable. The destination must have one predecessor and branch directly
to the other arm's join, which keeps the allocation singly executed and avoids
phi-edge dominance repair.

This is the same conservative control-flow-sinking principle used by LLVM's
`sink` pass and MLIR's `control-flow-sink`: move work into a successor/conditional
region only when paths that do not need the result can avoid it. The implementation
here is independent and adds PB-specific ownership and heap-order restrictions.

## What it needs

- Dominance: the allocation must be dead on every path that does not reach the
  use, and the sunk position must dominate all uses.
- No observable difference in **allocation order** — which matters here because a
  heap compaction is observable through `FRE()` and through the topmost-block
  test that the in-place append paths rely on
  ([O0208](O0208-inplace-literal-append.md)). Moving an allocation can therefore
  change which appends stay in place, so the pass must be measured, not just
  proven.

## Still planned

- General dominator/post-dominator based sinking into deeper regions and full
  diamonds, including phi repair where a result legitimately crosses a join.
- Other allocation families (dynamic arrays and string-producing routines whose
  arguments have ownership/consumption semantics) after their heap effects are
  explicit enough to prove safe movement.
- A whole-program heap-observer model for `FRE()` and the topmost-block tests used
  by in-place string growth, so wider movement can be justified rather than
  inferred from a local CFG alone.
