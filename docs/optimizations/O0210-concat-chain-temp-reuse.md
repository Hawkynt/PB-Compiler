# O0210 — Concat-chain dead-temp reuse

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter + string runtime |
| **Source** | `rt_strcatlit` / `rt_strcatvar` reused at chain nodes |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF97.BAS` |
| **Split from** | [O0009](O0009-string-temp-economy.md) |

## What it is

In a left-associative chain `a$ + b$ + c$` = `(a$ + b$) + c$`, the inner concat
produces a **fresh, dead, topmost** temp. The next barrier-free operand
(a literal or a bare variable) is therefore appended to it in place, instead of
allocating a new `StrCat` result at every node — O(n) rather than O(n²).

## Sample

```basic
DIM a$, b$, s$
s$ = a$ + b$ + "tail"
```

## Why it is safe

The left operand is a **temp**: operands are only read as raw handles, never
mutated, so appending into it cannot disturb anything the program can observe.
The runtime still checks topmost-ness and falls back to a copy otherwise.

## Relationship to O0024

For chains of **three or more** safe operands,
[O0024](O0024-multi-concat.md) does strictly better — one allocation for the
whole chain rather than in-place growth at each node — and subsumes this path.
This one still covers the two-operand-plus-tail shapes and any chain O0024
declines.
