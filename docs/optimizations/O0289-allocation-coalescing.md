# O0289 — Allocation coalescing

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Mid-end |
| **Pass** | `StringAllocationCoalescing` (`strcoalesce`) |
| **Related** | [O0024](O0024-multi-concat.md), [O0086](O0086-spill-slot-reuse.md), [O0286](O0286-allocation-elimination.md), [O0287](O0287-stack-promotion.md), [O0294](O0294-string-builder-recognition.md) |

## The idea

Several short-lived string allocations that occur together are treated as one bounded allocation
**region**. The middle end computes a conservative upper bound for the region, the DOS runtime
preflights that space once, and the recognized producers then carve ordinary PB string blocks from
the already-reserved tail instead of independently checking and compacting the heap for every
allocation.

[O0024](O0024-multi-concat.md) already removes intermediate allocations inside concatenation chains.
O0289 covers a different shape: several distinct results whose lifetimes overlap but whose individual
allocations can still share one allocation transaction.

## Applies to

```basic
DIM a$, b$, c$
a$ = LEFT$(src$, 10)
b$ = MID$(src$, 11, 10)
c$ = RIGHT$(src$, 10)
```

Normal string lowering reads `src$` through `rt_str_dup` because the ordinary substring routines
consume their source handles. In a profitable O0289 region, a single-use `rt_str_dup` feeding
`LEFT$`, `MID$`, or `RIGHT$` is removed and the call is rewritten to a borrowed coalesced substring
entry. The source variable remains owned by its storage cell while the three result blocks are carved
from the preflighted region.

## Implementation

`StringAllocationCoalescing` runs after the other string-shape passes so it sees their final allocation
pattern. It operates conservatively:

- only straight-line regions inside one basic block are considered;
- a region needs at least three recognizable allocations before the begin/end markers are worthwhile;
- every allocation must have a compile-time upper bound, and the total reservation must fit in a
  signed 16-bit byte count;
- `rt_str_free` is transparent inside a region, while every unknown or heap-sensitive call is a
  barrier;
- functions with PB error handlers or inline assembly are skipped because either can transfer control
  around the synthetic region end;
- already coalesced regions are treated as opaque, making the pass idempotent.

The currently bounded producer set is:

- string constants;
- `LEFT$`, `RIGHT$`, and the bounded three-argument `MID$`;
- `SPACE$`;
- `STRING$(count, code)`;
- `CHR$`.

Dynamic-length producers and routines that inspect or mutate `rt_strtop` directly remain barriers
until they can provide an equally strong capacity proof.

## DOS runtime representation

O0289 deliberately does **not** make several descriptors alias one physical string block. The DOS
runtime's ordinary representation is already coupled to exact `rt_str_free` behavior and heap
compaction, so aliasing headers would require a second ownership model throughout the string kernel.

Instead, `rt_str_coalesce_begin` preflights the conservative byte bound. If the current tail cannot
hold it, the runtime performs the normal compaction once before opening the region. Specialized
coalesced producers then allocate the same ordinary `[handle,length] + payload` blocks and the same
ordinary descriptors as before, but advance a private region cursor without repeating the heap-space
check. `rt_str_coalesce_end` commits the final cursor to `rt_strtop`.

If preflight or descriptor assumptions fail, the specialized allocator disarms the region and falls
back to the canonical `StrAlloc` path. Correctness therefore does not depend on optimizer metadata
remaining trustworthy at runtime.

This keeps freeing exact: each result still owns one normal PB string block and can be released at its
ordinary lifetime end. Later compaction also sees exactly the block format it already understands.

## Backend support

The synthetic O0289 runtime ABI is routed through `RuntimeAbi` on x86-16:

- the region reservation is passed in `CX`;
- string handles continue to use `AX`;
- substring start/count arguments retain the existing `CX`/`DX` conventions;
- constant bytes retain the existing `DS:SI` convention.

The hosted C/LLVM runtime has no compacting DOS string heap. Its begin/end markers are therefore
no-ops and its coalesced producer symbols forward to the ordinary hosted string operations. That lets
all backends consume the same optimized IR without pretending that the hosted allocator has DOS heap
semantics.

## Validation

`StringAllocationCoalescingTests` covers profitable substring regions, removal of single-use borrow
copies, unknown-call barriers, conservative capacity accounting, idempotence, and exclusion of
functions with non-local error control flow.

The design was cross-checked conceptually against LLVM's stack-coloring/lifetime-merging model. LLVM
is Apache-2.0 WITH LLVM-exception; no LLVM implementation code was copied or translated. The O0289
implementation is derived independently from PB-Compiler's own IR ownership rules and DOS string-heap
ABI.

## Remaining scope

This implementation intentionally stops at bounded basic-block regions. Cross-block lifetime
coalescing, dynamic reservation proofs, and additional string producers can be added later once their
control-flow and capacity proofs are explicit; they are not required for the implemented O0289
region form.