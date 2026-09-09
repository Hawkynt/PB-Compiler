# O0286 — Allocation elimination

| | |
|---|---|
| **Status** | 🟡 Partial (targeted cases: `ASC(MID$/LEFT$/RIGHT$)` reads the byte with no substring — [O0297](O0297-substring-view.md); `PRINT CHR$(n)` prints the byte with no 1-char string) |
| **Stage** | Mid-end |
| **Related** | [O0260](O0260-escape-analysis.md), [O0059](O0059-scalar-replacement.md), [O0287](O0287-stack-promotion.md), [O0009](O0009-string-temp-economy.md) |

## The idea

A heap allocation whose contents can live entirely in registers or a frame slot
should not happen at all. For string-heavy BASIC this is the largest class of
avoidable work in the program: every intermediate string is a `StrMem`
allocation, a copy, and a `StrFree` — and a compaction risk.

## Applies to

```basic
DIM s$, n%
n% = LEN(LEFT$(s$, 3))       ' the substring is allocated, measured, and freed
```

The temporary exists only to have its length taken.

## Now

The general escape-analysis-driven elimination is not built yet, but two of its most
common instances ship as targeted rewrites in the emitter:

- **`PRINT CHR$(n)`** prints the single byte (`n AND 255`) directly through the same
  `rt_print_str` the string-literal path uses, on a one-byte scratch cell — no
  1-char string is allocated and freed just to print it. This is the control-code
  idiom (`PRINT CHR$(13); CHR$(10)`, `CHR$(27)` escape sequences, `CHR$(7)` bell).
  Optimize-gated, so the faithful build keeps the `rt_chr` allocation and
  `rt_str_print`; verified by a DOSBox self-diff (printable and computed bytes,
  optimizer on and off) and an `absent-call rt_str_print` byte assertion.
- **`ASC(MID$(s$, i, 1))`** and its `LEFT$`/`RIGHT$`/two-argument siblings read the
  byte straight from the source buffer with no substring allocation — see
  [O0297](O0297-substring-view.md).

## What it needs

- [O0260](O0260-escape-analysis.md) to prove the value never escapes, plus a
  **use analysis** showing every use is satisfiable without materializing the
  object (a length, a byte compare, a single character).
- Per-intrinsic rewrite rules: `LEN(LEFT$(s,n))` is `MIN(n, LEN(s))`,
  `ASC(MID$(s,i,1))` is a byte load, `LEFT$(a,n) = LEFT$(b,n)` is a bounded
  compare ([O0297](O0297-substring-view.md) generalizes this).
- Where the object is needed but not on the heap, the answer is
  [O0287](O0287-stack-promotion.md).
