# O0309 — Speculative integer narrowing

| | |
|---|---|
| **Status** | 🟡 Partial — guarded scalar loop versioning |
| **Stage** | Mid-end |
| **IR** | 🟡 `Ir/Passes/SpeculativeIntegerNarrowing.cs` — SPEED-gated canonical loop versioning behind invariant scalar word-range guards; aggregate/profile guards and widening-result arithmetic remain planned |
| **Source** | `Ir/Passes/SpeculativeIntegerNarrowing.cs` |
| **Gate** | `$OPTIMIZE SPEED` |
| **Verified by** | `SpeculativeIntegerNarrowingTests` |
| **Related** | [O0221](O0221-operation-narrowing.md) operation narrowing, [O0057](O0057-storage-narrowing.md) storage narrowing, [O0304](O0304-guarded-specialization.md) guarded specialization |

## What is implemented

O0221 narrows a 32-bit operation when the value lattice can prove the required word range statically.
O0309 now covers the next conservative case for the IR path: a **canonical single-exit loop** whose repeated
32-bit work depends on a small number of loop-invariant scalar values.

Under `$OPTIMIZE SPEED`, the pass may:

1. put one signed `-32768..32767` or unsigned `0..65535` range guard for each invariant scalar in the preheader;
2. clone the loop once;
3. execute a narrowed fast clone when every guard succeeds;
4. execute the original wide loop unchanged when any guard fails; and
5. form the required exit phis so both versions remain valid SSA.

The current profitability fence accepts at most two guarded scalars and at most 96 instructions in the cloned
region. A multiply is enough to justify versioning; otherwise at least two narrowable operations must share the
guard. The ordinary optimization objective does not version the loop because the transform deliberately spends
code size for speed.

The operations admitted in the fast clone are:

- `i32/u32` comparisons whose two operands fit the corresponding 16-bit range;
- `i32/u32` `add`, `sub`, and `mul` when **both operands and the mathematical result** fit that range.

Each narrow arithmetic result is sign- or zero-extended back to the original 32-bit SSA type, so users outside
the operation keep exactly the same type and value.

## Safety boundary

The runtime guard is allowed only for a scalar SSA value available before the loop. A loop-local load is not a
guard source: checking an array element-by-element before entering the loop would normally cost more than the
narrowing saves and could change observable memory behavior. Declared-range/profile-driven aggregate guards
remain future work.

The result-range requirement is also intentional. The IR currently has no widening integer multiply such as
`i16 * i16 -> i32`; its `IrBinary` result has the same width as its operands. Therefore transforming a wide
multiply merely because both operands fit a word could introduce a 16-bit wrap that the original 32-bit
operation did not have. The fast path is emitted only when interval arithmetic proves that cannot happen.

That means the motivating shape

```basic
DIM i%, a&(0 TO 999), s&

FOR i% = 0 TO 999
  s& = s& + a&(i%) * 2
NEXT
```

is **not yet fully covered**: proving every `a&(i%)` narrow would require a cheap declared/profile aggregate
range fact, and `a&(i%) * 2` needs a widening-result representation when the product may exceed one word.
Until both exist, the pass declines rather than silently change wrap semantics.

## Shape restrictions

The first implementation deliberately accepts only regions the existing `IrCloner` can duplicate safely:

- one loop header with an unconditional preheader edge;
- one unconditional latch edge;
- one exit, reached only from the header;
- no outside entry into the body;
- no address-taken block in the cloned region;
- no error-handler or inline-assembly function.

The original loop is retained as the fallback. This also makes the transform self-limiting under the pass
manager's fixpoint: after versioning, the preheader is conditional and no longer matches the input shape.

## References and licensing

The implementation is original managed C# and adds no dependency. External material was consulted for
legality/profitability architecture only; no implementation code was copied:

- LLVM InstCombine contributor guide — integer type-width changes must be legal and profitable:
  <https://llvm.org/docs/InstCombineContributorGuide.html>
- MLIR `arith-int-range-narrowing` — range-analysis-driven integer narrowing as an established compiler
  technique:
  <https://reviews.llvm.org/D139525>

LLVM is Apache-2.0 WITH LLVM-exception; PB-Compiler is LGPL-3.0-or-later. The pass is independently implemented
against PB-Compiler's own IR, range analysis, dominator analysis, and cloning primitives.
