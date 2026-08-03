# O0208 — In-place literal append

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter + string runtime |
| **Source** | runtime `rt_strcatlit` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF94.BAS` |
| **Split from** | [O0009](O0009-string-temp-economy.md) |

## What it is

`s$ = s$ + "literal"` calls `rt_strcatlit`, which — when `s$` is the **topmost**
heap block and there is room under the `$STRING` cap — appends the literal's
bytes straight after `s$`'s data and grows the block in place, keeping the
**same handle**.

A literal needs no heap temp, so `s$` stays topmost across loop iterations: an
O(n) build loop costs O(n) in total instead of recopying the whole string at
every append.

## Sample

```basic
DIM s$, i%
FOR i% = 1 TO 1000
  s$ = s$ + "x"
NEXT
```

## Cost

| | allocations | bytes copied |
|---|---|---|
| without | 1 000 | ≈ 500 000 |
| with | 0 (after the first) | 1 000 |

## Why it is safe

When `s$` is **not** topmost the routine falls back to the exact `StrMem` +
`StrCat` path, so the resulting string and the heap state are identical either
way — only the allocation count differs. The `$STRING` cap is checked before any
in-place growth.
