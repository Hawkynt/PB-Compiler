# O0332 — Lookup-table generation

| | |
|---|---|
| **Status** | 🟡 Partial — repeated dynamic calls to sufficiently expensive pure one-byte integer functions can become a complete 256-byte table |
| **Stage** | Mid-end |
| **Source** | `Ir/Passes/LookupTableGeneration.cs` |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` |
| **Verified by** | `DataRepresentationOptimizationTests` |
| **Related** | [O0025](O0025-pure-function-folding.md), [O0132](O0132-compile-time-loop-evaluation.md), [O0333](O0333-lookup-table-elimination.md) |

## The idea

A **pure** function over a **small domain** can be evaluated at compile time for
every input and emitted as a table. The call becomes an indexed load.

## Implemented v1

`LookupTableGeneration` handles a deliberately narrow, bit-exact case: one
8-bit integer parameter, one 8-bit integer result, one basic block, and an
integer-only expression subset (`+`, `-`, `*`, bitwise ops, integer comparisons,
selects and integer casts). It evaluates all 256 input bit patterns itself.

A table is generated only when there are at least two calls and at least one is
dynamic; trivial bodies are rejected so a 256-byte object is not emitted for a
cheaper calculation. Existing same-named globals are reused only when their
layout and all 256 bytes match exactly.

## Applies to

```basic
FUNCTION Scramble&&(BYVAL x??)
  ' representative small-domain pure integer transform
END FUNCTION
```

Repeated runtime calls over a byte-valued input can become indexed loads from a
compiler-generated `.lut.*` object.

## Still planned

- Ranges other than the full byte domain, using proven parameter ranges.
- Wider result types where the table-size budget still wins.
- Multi-block pure functions and reuse of the broader compile-time evaluator.
- Floating-point tables once the evaluator can reproduce the runtime's exact FP
  semantics.
- A target cost model rather than the current conservative body/call threshold.
