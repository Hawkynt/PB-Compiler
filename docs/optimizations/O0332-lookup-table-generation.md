# O0332 — Lookup-table generation

| | |
|---|---|
| **Status** | 🟡 Partial — range-aware integer LUT generation is implemented; wider result storage and FP tables remain |
| **Stage** | Mid-end |
| **Source** | `Ir/Passes/LookupTableGeneration.cs` |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` |
| **Verified by** | `DataRepresentationOptimizationTests`, `LookupTableGenerationTests` |
| **Related** | [O0025](O0025-pure-function-folding.md), [O0132](O0132-compile-time-loop-evaluation.md), [O0333](O0333-lookup-table-elimination.md) |

## The idea

A **pure** function over a **small domain** can be evaluated at compile time for
every input and emitted as a table. The call becomes an indexed load.

## Implemented integer path

`LookupTableGeneration` handles one integer parameter up to 16 bits and one
8-bit integer result. It uses `IrRangeAnalysis` at every dynamic call site and
forms one contiguous union domain; the table is emitted only when that proven
domain contains at most 256 values. An unconstrained 8-bit parameter retains the
complete 256-pattern fallback used by the original implementation.

The compile-time evaluator is control-flow aware. It interprets integer
arithmetic, division/remainder, bitwise operations, shifts, comparisons, integer
casts, selects, branches, switches and phis across multiple basic blocks. Phi
updates are simultaneous on an incoming edge, so bounded loops can be evaluated
as well as diamonds. Evaluation has a hard step budget and fails closed on an
unsupported instruction, invalid shift, division fault, non-termination beyond
the budget, or any side-effecting operation.

A table is generated only when at least **two dynamic calls** are replaced.
Constant calls do not count toward profitability and are left for ordinary
constant/pure-function folding. This fixes the old case where one dynamic call
plus one literal call could spend 256 bytes to remove only one runtime call.

For narrowed domains the generated index is normalized by subtracting the
proven lower bound. Signed and unsigned call-site ranges are only used when their
interpretation agrees with the formal parameter; otherwise an 8-bit parameter
falls back to a complete raw-pattern table and a wider parameter is declined.
This avoids treating one two's-complement bit-pattern set as the wrong
mathematical interval.

Generated tables keep the historical `.lut.<function>` name for a full byte
domain. Range-specialized tables include the parameter shape, raw starting
pattern and count in the symbol name. Existing same-named globals are reused
only when their storage, element count and initializer bytes match exactly.

## Example

```basic
FUNCTION Scramble&&(BYVAL x??)
  ' representative small-domain pure integer transform
END FUNCTION
```

Repeated runtime calls over a byte-valued input can become indexed loads from a
compiler-generated `.lut.*` object. The same applies to a WORD/INTEGER parameter
when caller-side range analysis proves, for example, that every dynamic call is
confined to `10..25`; only sixteen table entries are then materialized.

## Safety and semantics

Integer evaluation follows the IR's fixed-width two's-complement semantics:
`+`, `-` and `*` wrap at each result width, signed and unsigned comparisons read
the same bit pattern according to their predicate, and `trunc`/`zext`/`sext`
preserve the repository's existing conversion rules. Division by zero,
`INT_MIN / -1`, `INT_MIN MOD -1` and out-of-range shifts abandon generation so a
runtime fault is never compiled away.

The implementation is clean-room C# over PB-Compiler's own IR. LLVM's Language
Reference was consulted only to cross-check the fixed-width integer, shift and
conversion semantics; no LLVM implementation code was copied or translated.
LLVM is Apache-2.0 WITH LLVM-exception and PB-Compiler remains LGPL-3.0-or-later.

## Still planned

- Wider integer result types once the IR has a target-independent typed integer
  constant-array initializer rather than only byte blobs.
- Floating-point tables once the evaluator can reproduce the runtime's exact FP
  semantics for arbitrary user functions; O0343 already owns its narrower
  proven-domain FP specialization path.
- A target cost model instead of the current conservative body/call/table-size
  thresholds.
- Richer pure operations/callee composition if a shared IR compile-time
  interpreter is introduced.
