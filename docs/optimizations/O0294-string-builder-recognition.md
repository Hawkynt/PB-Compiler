# O0294 — String-builder recognition

| | |
|---|---|
| **Status** | ⬜ Planned (the self-append shape is already O(n) — [O0208](O0208-inplace-literal-append.md)) |
| **Stage** | Mid-end |
| **Related** | [O0208](O0208-inplace-literal-append.md), [O0024](O0024-multi-concat.md), [O0290](O0290-loop-temporary-reuse.md) |

## The idea

Repeated concatenation in a loop is quadratic when each step reallocates and
recopies. The in-place append paths already fix the shapes where the
accumulator stays the topmost heap block; the general recognizer would handle the
rest — **geometric growth** (reserve, double when full) or a **two-pass** build
(measure, allocate once, fill).

## Applies to

```basic
DIM i%, out$, parts$(0 TO 999)
FOR i% = 0 TO 999
  out$ = out$ + parts$(i%) + ","    ' not always topmost -> falls back to copying
NEXT
```

## What it needs

- Recognition of the accumulator pattern across the loop, including the cases
  where another allocation inside the body displaces the accumulator from the
  top of the heap.
- A **capacity** concept in the string manager (length ≠ allocated size), which
  it does not have today — that single change is what makes geometric growth
  expressible, and it also unblocks
  [O0353](O0353-string-capacity-hoisting.md).
- The observable result must be identical, including `FRE()` behaviour if the
  program inspects the heap.
