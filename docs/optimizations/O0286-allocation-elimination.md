# O0286 — Allocation elimination

| | |
|---|---|
| **Status** | 🟡 Partial (targeted cases: `LEN(LEFT$/RIGHT$/MID$)` derives the result length without a substring allocation; `ASC(MID$/LEFT$/RIGHT$)` reads the byte with no substring — [O0297](O0297-substring-view.md); `PRINT CHR$(n)` prints the byte with no 1-char string) |
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

The general escape-analysis-driven elimination is not built yet, but common cases
ship as targeted rewrites:

- **`LEN(LEFT$(s$, n))`** and **`LEN(RIGHT$(s$, n))`** are reduced in the IR to
  `MIN(MAX(n, 0), LEN(s$))`. The source length is read at the original substring
  call site so the consuming runtime-handle lifetime and observable call ordering
  remain unchanged.
- **`LEN(MID$(s$, start, n))`** and **`LEN(MID$(s$, start))`** likewise become scalar
  length arithmetic. The rewrite preserves the runtime's 1-based start semantics:
  starts below 1 clamp to 1, starts beyond the source return zero, non-positive
  explicit lengths return zero, and lengths past the remainder clamp to the end.
  Nested length-only slices are eliminated to a fixpoint.
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

The IR rewrite only fires when the fresh substring has exactly one user and that
user is `rt_str_len`; a value with any other user still materializes normally.

## What it needs

- [O0260](O0260-escape-analysis.md) for the general case, plus a **use analysis**
  showing every use is satisfiable without materializing the object (a length, a
  byte compare, a single character). The targeted length rule above does not need
  a separate escape analysis because its exact single user proves non-escape.
- Further per-intrinsic rewrite rules, for example bounded comparisons such as
  `LEFT$(a,n) = LEFT$(b,n)` ([O0297](O0297-substring-view.md) generalizes this).
- Where the object is needed but not on the heap, the answer is
  [O0287](O0287-stack-promotion.md).
