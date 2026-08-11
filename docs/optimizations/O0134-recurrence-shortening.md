# O0134 — Loop-carried dependency shortening and closed forms

| | |
|---|---|
| **Status** | ⬜ Planned (the arithmetic-series closed form exists — [O0020](O0020-idiom-replacement.md)) |
| **Stage** | Mid-end |
| **IR** | 🟡 `Ir/Passes/RecurrenceClosedForm.cs` — the CLOSED-FORM half, for the constant-step case: an accumulator whose only work is adding a constant is `start + step * trips`, computed once instead of iterated. It is not unrolling with extra steps - `LoopUnroll` replaces a loop with copies and is capped because the copies are the cost, while a closed form does not care whether the trip count is four or forty thousand, so the two cover different loops. Restricted to INTEGER accumulators, and that restriction IS the soundness argument: two's-complement addition is associative across wrapping, so accumulating n times and multiplying by n agree even when the steps overflow, whereas floating point rounds at every step and a sum of forty roundings is not one multiplication. The accumulator must also be unread inside the loop, or its intermediate values are observable. Placed after `dce`, because `IntegerRecovery` leaves the float-shaped arithmetic standing beside the integer form and that shadow counts as a reader. The SHORTENING half is not done. Verified by `RecurrenceClosedFormTests` and `IrPassObservableEquivalenceTests` |
| **Related** | [O0020](O0020-idiom-replacement.md), [O0119](O0119-reduction-recognition.md), [O0121](O0121-reduction-tree-balancing.md) |

## The idea

Two related treatments of a loop-carried recurrence:

1. **Shortening** — rewrite the recurrence so that the distance between a
   producer and its next-iteration consumer is as small as possible. A chain
   whose latency exceeds the rest of the body's work is what makes a loop
   dependence-bound rather than throughput-bound.
2. **Closed form** — replace the recurrence outright where the mathematics
   permits: `s = s + i` over `1..n` is `n(n+1)/2` (already done for constant
   bounds by [O0020](O0020-idiom-replacement.md)), `x = x * 2` is a shift,
   `x = x + k` is `x0 + n*k`.

## Applies to

```basic
DIM i%, s&, n%
FOR i% = 1 TO n%
  s& = s& + i%               ' closed form: n(n+1)/2, even for a variable n%
NEXT
```

## Today

Folded only when both bounds are compile-time constants; a variable limit runs
the loop.

## Planned

```asm
    mov     ax, [n]
    mov     bx, ax
    inc     bx
    imul    bx               ; n*(n+1)
    sar     ax, 1            ; \2  — the whole loop
```

## What it needs

- **Overflow and wrap semantics decide legality.** `n(n+1)/2` equals the summed
  value only if the sum never wrapped differently along the way — which for
  16-bit accumulation it does. So the closed form needs the exact-range proof
  from [O0016](O0016-value-fact-analysis.md), and under `$ERROR OVERFLOW` it must
  also preserve *which iteration* would have overflowed first.
- Recurrence classification over the SSA loop phi (the same analysis
  [O0110](O0110-general-induction-variables.md) needs).
