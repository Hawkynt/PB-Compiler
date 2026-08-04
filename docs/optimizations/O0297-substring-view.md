# O0297 — Substring as a view

| | |
|---|---|
| **Status** | 🟡 Partial (the single-character `ASC(MID$(s$, i, 1))` / `ASC(s$, i)` view is a direct byte read; the general compare/print view is not) |
| **Stage** | Mid-end + emitter |
| **Related** | [O0286](O0286-allocation-elimination.md), [O0293](O0293-copy-on-write-elision.md), [O0298](O0298-string-compare-length-guard.md) |

## The idea

`LEFT$`, `RIGHT$` and `MID$` allocate a copy. When the result is **temporary and
read-only** — compared, measured, printed, or scanned — a pointer-and-length
*view* into the original storage does the same job with no allocation and no
copy.

## Applies to

```basic
DIM s$
IF MID$(s$, 5, 3) = "abc" THEN ...     ' allocates 3 bytes to compare 3 bytes
PRINT LEFT$(s$, 10)                    ' allocates 10 bytes to print them
```

## Now

The most common **single-character** view ships: `ASC(MID$(s$, i, 1))` and the
two-argument `ASC(s$, i)` — a one-character substring immediately consumed by `ASC`
— read the byte straight from the source buffer (`rt_charat`, `EmitCharAt`) instead
of allocating a one-character string and reading its head. In a character-scan loop
that removes one heap allocation *per character*. It reproduces `MID$`'s edge
behaviour exactly (start clamps to 1, a start past the end yields 0), and consumes
(frees) the operand like the substring path it replaces. `rt_charat` lives in its
own trimmed section referenced only by the optimized emitter, so the faithful build
keeps the substring form byte-for-byte (golden gate 250/250). Verified by a
self-differential DOSBox run over every edge — `i` in range, `i < 1` (clamped),
`i > LEN`, an empty string, first and last character — identical to `$OPTIMIZE OFF`.

## Still planned

- The general read-only view for `LEFT$`/`RIGHT$`/`MID$` results that are compared
  (compose with [O0298](O0298-string-compare-length-guard.md)), printed, or measured
  — a segment/offset/length view the emitter passes without entering the string
  manager, with the expression-local lifetime rule below.

## What it needs

- A **view representation** the emitter can pass to the comparison, print and
  scan paths without entering the string manager — segment, offset, length.
- A lifetime rule: the view is only valid while the base string is unchanged and
  unmoved, so any allocation (which can compact the heap) between creation and
  use invalidates it. That makes views strictly *expression-local* unless
  [O0260](O0260-escape-analysis.md) proves more.
- The fallback stays the real substring, so nothing is lost where the view is
  illegal.
