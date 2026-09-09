# O0287 — Stack promotion

| | |
|---|---|
| **Status** | 🟡 Partial — exact-size, non-escaping PRINT temporaries up to 64 bytes |
| **Stage** | Mid-end |
| **IR** | ✅ `Ir/Passes/StringStackPromotion.cs`, wired after string concat/append canonicalization in `IrPassManager.Standard()` |
| **Related** | [O0260](O0260-escape-analysis.md), [O0286](O0286-allocation-elimination.md), [O0182](O0182-small-array-scalar-replacement.md) |

## The idea

A non-escaping dynamic allocation of **bounded size** can live in the frame
instead of the heap: no `StrMem`/`StrFree`, no descriptor-table slot, no
compaction pressure, and the epilogue reclaims it for free.

The first implemented slice keeps the representation proof deliberately narrow.
PowerBASIC's DOS dynamic string is a **handle into the runtime descriptor table**,
not a pointer to its bytes. An `alloca` therefore cannot simply replace a string
handle. `StringStackPromotion` only crosses that boundary when it controls the
complete value from construction through consumption.

## Implemented slice

The pass recognizes exact-size producer trees made only from:

- `CHR$` (one byte),
- pooled string literals,
- pairwise concatenation,
- O0352's literal-append form, and
- O0024's multi-concat form.

A tree is promotable only when every producer has exactly one user, every
producer is in the same basic block as the consumer, the complete value is no
larger than **64 bytes**, and the sole final use is `PRINT` or `PRINT #`.
Stores, returns, phis, BYREF uses, unknown calls, second readers, error-handler
functions and inline-assembly functions all fail closed.

The bytes are materialized into an `alloca i8, N`. The x86-16 raw-print ABI is
older than that representation: `rt_print_str` takes a DS-relative literal-style
address while an `alloca` is SS-relative. The pass therefore performs one final
raw `memcpy` from the stack object into a compiler-owned 64-byte DS staging
buffer immediately before `rt_print_str` / `rt_fprint_str`. No string descriptor,
heap block, allocation or free is created. C and LLVM see the same ordinary byte
copy, so the IR remains target-neutral.

LLVM's `alloca` lifetime model is the reference for the frame object: storage is
part of the current function's stack frame and is reclaimed on return. The pass's
escape rule is intentionally stricter than LLVM's general pointer-capture model
until O0260 provides a shared repository-wide analysis.

## Example reached today

```basic
SUB EmitTag(BYVAL code&)
  PRINT CHR$(code&) + ":"
END SUB
```

The temporary two-byte dynamic string is built in the frame and printed as raw
bytes; the string heap is not involved.

## Still pending

The broader motivating shape remains future work:

```basic
SUB Format(BYVAL n&)
  LOCAL t$
  t$ = STR$(n&)              ' at most 12 bytes, never leaves this procedure
  PRINT t$
END SUB
```

It needs both a bounded raw formatter for numeric `STR$` and a representation
proof for a local string variable that remains handle-visible in today's IR.
O0260 is the natural shared home for the latter. Substrings with data-dependent
lengths and other handle-visible temporaries likewise stay on the string heap.

## Correctness constraints

- A promoted object must have a conservative size bound within the 64-byte frame
  budget.
- No promoted byte pointer may be passed where a dynamic-string **handle** is
  expected.
- An escaping or multiply-observed value must stay a handle.
- `ON ERROR` / `RESUME` functions are excluded; their hidden control flow and
  handle cleanup cannot observe a frame-backed pseudo-string.
- The DS staging buffer is only used in the straight-line copy-then-print
  sequence; nothing can observe it between those operations.
