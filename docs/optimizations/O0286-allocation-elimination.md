# O0286 — Allocation elimination

| | |
|---|---|
| **Status** | ⬜ Planned |
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

## What it needs

- [O0260](O0260-escape-analysis.md) to prove the value never escapes, plus a
  **use analysis** showing every use is satisfiable without materializing the
  object (a length, a byte compare, a single character).
- Per-intrinsic rewrite rules: `LEN(LEFT$(s,n))` is `MIN(n, LEN(s))`,
  `ASC(MID$(s,i,1))` is a byte load, `LEFT$(a,n) = LEFT$(b,n)` is a bounded
  compare ([O0297](O0297-substring-view.md) generalizes this).
- Where the object is needed but not on the heap, the answer is
  [O0287](O0287-stack-promotion.md).
