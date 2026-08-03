# O0292 — Ownership operation batching

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0291](O0291-handle-ownership-elision.md), [O0290](O0290-loop-temporary-reuse.md), [O0028](O0028-loop-invariant-code-motion.md) |

## The idea

Where a dup/free pair cannot be removed, it can often be **moved out of a
loop**: acquire once before, release once after, instead of per iteration.
Repeated operations on the same handle within a straight-line run likewise
collapse to one.

## Applies to

```basic
DIM i%, s$, t$
FOR i% = 1 TO 1000
  t$ = s$                    ' dup + free every iteration, same value each time
  PRINT LEFT$(t$, i%)
NEXT
```

## What it needs

- The loop-invariance test [O0028](O0028-loop-invariant-code-motion.md) already
  performs, applied to the ownership operations rather than to arithmetic.
- The zero-trip and early-exit obligations: a handle acquired in the preheader
  must be released on **every** exit path, including `EXIT FOR`, `GOTO` out and
  an error handler — which is exactly why this is harder than hoisting an
  arithmetic expression.
