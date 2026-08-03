# O0134 — Loop-carried dependency shortening and closed forms

| | |
|---|---|
| **Status** | ⬜ Planned (the arithmetic-series closed form exists — [O0020](O0020-idiom-replacement.md)) |
| **Stage** | Mid-end |
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
