# O0297 — Substring as a view

| | |
|---|---|
| **Status** | ⬜ Planned |
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

## What it needs

- A **view representation** the emitter can pass to the comparison, print and
  scan paths without entering the string manager — segment, offset, length.
- A lifetime rule: the view is only valid while the base string is unchanged and
  unmoved, so any allocation (which can compact the heap) between creation and
  use invalidates it. That makes views strictly *expression-local* unless
  [O0260](O0260-escape-analysis.md) proves more.
- The fallback stays the real substring, so nothing is lost where the view is
  illegal.
