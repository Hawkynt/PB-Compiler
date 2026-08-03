# O0288 — Allocation sinking

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0253](O0253-store-sinking.md), [O0286](O0286-allocation-elimination.md), [O0105](O0105-hot-cold-splitting.md) |

## The idea

An allocation performed unconditionally but used only on a rare path should
happen **on that path**. The common execution then pays nothing.

## Applies to

```basic
DIM msg$, err%
msg$ = "operation failed: code "     ' allocated every call
IF err% THEN PRINT msg$; err%        ' used almost never
```

## What it needs

- Dominance: the allocation must be dead on every path that does not reach the
  use, and the sunk position must dominate all uses.
- No observable difference in **allocation order** — which matters here because a
  heap compaction is observable through `FRE()` and through the topmost-block
  test that the in-place append paths rely on
  ([O0208](O0208-inplace-literal-append.md)). Moving an allocation can therefore
  change which appends stay in place, so the pass must be measured, not just
  proven.
