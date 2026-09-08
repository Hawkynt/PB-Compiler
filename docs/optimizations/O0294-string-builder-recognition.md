# O0294 — String-builder recognition

| | |
|---|---|
| **Status** | 🟡 Partial (loop-carried variable/literal concat builders) |
| **Stage** | Mid-end |
| **IR** | `PowerBasic.Compiler/Ir/Passes/StringBuilderRecognition.cs` |
| **Related** | [O0208](O0208-inplace-literal-append.md), [O0024](O0024-multi-concat.md), [O0290](O0290-loop-temporary-reuse.md), [O0353](O0353-string-capacity-hoisting.md) |

## The idea

Repeated concatenation in a loop is quadratic when each step allocates a new
result and recopies the prefix accumulated so far. O0208 already has runtime
entries that can grow the accumulator in place while it remains the topmost heap
block. The missing interaction was O0024: a three-or-more operand expression was
collapsed to `rt_str_concat_n` before the append pass saw it, so a builder still
allocated a fresh whole result once per iteration.

The implemented O0294 slice recognizes the accumulator as a pointer `phi` whose
natural-loop backedge is a left-associated concat chain rooted in a borrow of the
same `phi`. Variable and literal suffixes are then rewritten to
`rt_str_append_var` / `rt_str_append_lit` before O0024 runs. The accumulator's
redundant `rt_str_dup` and assignment `rt_str_free` disappear together: the first
append consumes the old handle, while variable suffixes are borrowed and literals
are copied from their pooled bytes without materializing string handles.

That matters because those suffix temporaries were themselves enough to displace
the accumulator from the top of the DOS string heap.

## Applies to

```basic
DIM i%, out$, parts$(0 TO 999)
FOR i% = 0 TO 999
  out$ = out$ + parts$(i%) + ","
NEXT
```

The `parts$(i%)` read normally lowers through `rt_str_dup`, which allocates a
block above `out$`. O0294 proves that the append runtime only needs to borrow that
source and removes the duplicate; the comma likewise stays as literal bytes.
The two pairwise concatenations therefore become two append calls rather than one
`rt_str_concat_n` allocation that recopies `out$` every iteration.

## Safety boundary

The transform requires all of the following:

- a natural-loop backedge: the `phi` header dominates the latch;
- at least three operands, so ordinary two-operand self-append remains O0208;
- a left-associated chain whose suffix leaves are only `rt_str_dup` variable
  borrows or `rt_str_const` literals;
- private concat intermediates (no shared temporary ownership);
- inside the loop, the old accumulator is used only by the recognized borrow and
  by the matching assignment `rt_str_free`, which must occur after the completed
  concat expression in the same block;
- no error handler or inline assembly, matching the rest of the optimizer's CFG
  safety gates.

Any additional in-loop accumulator read, string-producing call suffix, shared
concat node, irreducible/non-dominated cycle, or different ownership shape leaves
the original expression untouched.

## What remains

This does **not** add spare capacity to the DOS string representation. A builder
whose body performs some other allocation before the first append can still make
the runtime take its allocate-and-copy fallback. The fully general form therefore
still needs either geometric growth (logical length distinct from allocated
capacity) or a safe two-pass measure/fill transform.

That representation change cannot be copied blindly from hosted `StringBuilder`
implementations: PB's heap is observable. In particular, a future implementation
must decide how ordinary `FRE()` queries account for reserved-but-unused bytes.
Today the routed IR does not lower ordinary `FRE()` (only the EMS `FRE(-11)`
form), so this allocation-elimination slice cannot move an observable heap query
inside an optimized function.

## References / licensing

The implementation is clean-room C# against PB-Compiler's existing IR/runtime
contracts; no external implementation code was copied or translated.

- LLVM, *Loop Terminology and Canonical Forms*: used for the natural-loop
  header/latch/backedge dominance terminology and proof shape. LLVM is
  Apache-2.0 WITH LLVM-exception.
- Microsoft .NET `StringBuilder.Capacity` documentation: used only as a
  conceptual reference for the remaining logical-length-versus-capacity design;
  no .NET source implementation is reused.
