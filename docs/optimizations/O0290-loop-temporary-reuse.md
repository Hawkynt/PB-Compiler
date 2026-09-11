# O0290 — Temporary reuse across loop iterations

| | |
|---|---|
| **Status** | 🟡 Partial — `StringByteRead` is the IR half of the direct emitter's pb36 O0297/O0290, and `LoopTemporaryReuse` covers the fixed-size array-heap temporary in a counted loop; the general string case is not done |
| **Stage** | Mid-end |
| **Related** | [O0068](O0068-array-zero-fill-elision.md), [O0286](O0286-allocation-elimination.md), [O0009](O0009-string-temp-economy.md), [O0329](O0329-array-contraction.md) |

## The idea

A temporary allocated and freed inside a loop body is allocated and freed **once
per iteration**. If its size is stable, the storage can be allocated once before
the loop and reused — turning N allocations into one.

This is the general form of what [O0208](O0208-inplace-literal-append.md)
achieves for the specific self-append shape.

## Implemented slice

`LoopTemporaryReuse` handles fixed-size numeric dynamic-array heap temporaries in
a canonical counted loop. It moves the loop's single `rt_arr_alloc(bytes)` to the
preheader and the matching `rt_arr_free(ptr, bytes)` to the exit when all of the
following are proven:

- the byte count is a positive compile-time constant and the loop executes more
  than once; explicit REDIM extent arithmetic is evaluated with the shared IR
  constant-folding semantics even though O0290 runs before ordinary InstCombine;
- the temporary lifetime lives in one work block, either as the latch itself or
  followed by PB lowering's dedicated `for.inc` latch containing only the induction
  update and branch;
- the allocation address and all derived GEPs stay inside the allocation/free
  lifetime and are used only for direct loads/stores;
- the work block contains no other calls, so no other allocation can get above
  this block in PB's topmost-only bump allocator and no callee can observe its address;
- every load from the buffer is covered by stores from the **current** iteration.
  This last proof preserves `rt_arr_alloc`'s zero-fill semantics: bytes left by a
  previous iteration can never become newly observable;
- the header contains only phi selection, the counted-loop comparison and branch,
  so moving allocation ahead of it cannot reorder an independent trap/error ahead
  of a possible out-of-memory failure.

The pass runs after `mem2reg` and the O0320–O0329 data-layout transforms, but
before [O0308](O0308-speculative-overflow-elimination.md)'s loop versioning and
before loop unrolling. That ordering exposes descriptor-held allocation results
as SSA values while keeping the original one-allocation loop lifetime intact:
both of the later passes duplicate the loop body, and a cloned lifetime is no
longer the single alloc/free pair this proof needs.

## Applies to

The implemented case is a scratch dynamic array whose contents are completely
written before they are read on each trip, for example a fixed-size `REDIM` /
`ERASE` buffer used only inside a counted `FOR` loop.

The broader motivating string case remains useful:

```basic
DIM i%, line$
FOR i% = 1 TO 1000
  line$ = "row " + STR$(i%)  ' a fresh temp every iteration
  PRINT line$
NEXT
```

but PB's current dynamic-string representation stores an exact block length and
has no spare-capacity field. Reusing a general string buffer across changing
lengths therefore needs either a runtime/descriptor representation change or a
specialized builder path; it cannot be implemented soundly by merely moving the
existing `rt_str_*` calls. [O0353](O0353-string-capacity-hoisting.md) already
covers one exact-trip append-builder shape without changing that ABI.

## What remains

- Extend the matcher to conditional/multi-block temporary lifetimes while proving
  allocation dominance and deallocation post-dominance over every iteration.
- Support bounded/grow-on-demand string temporaries once their representation can
  retain capacity independently of logical length.
- Consider pointer-element arrays once the pass has target data-layout facts; the
  current implementation deliberately refuses pointer-width coverage proofs in
  target-neutral IR.
