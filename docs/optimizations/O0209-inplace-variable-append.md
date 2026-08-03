# O0209 — In-place variable append

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter + string runtime |
| **Source** | runtime `rt_strcatvar` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF95.BAS` |
| **Split from** | [O0009](O0009-string-temp-economy.md) |

## What it is

`s$ = s$ + v$` (with `v$` a bare string variable) reads `v$`'s **raw handle** —
no `StrDup` temp, so `s$` stays topmost — and `rt_strcatvar` copies `v$`'s bytes
heap-to-heap straight after `s$`, leaving `v$` intact.

## Sample

```basic
DIM s$, v$
s$ = s$ + v$
s$ = s$ + s$                 ' the self-double case, also covered
```

## Why it is safe

- When `s$` is not topmost, the routine falls back to `StrDup` + `StrCat`, so
  the result is always identical.
- The **self-double** `s$ = s$ + s$` works because the destination begins exactly
  where the source ends and `REP MOVSB` copies forward — no byte is read after
  being overwritten.
- `v$` is only read, never consumed, which is what makes reading its raw handle
  (rather than a duplicate) correct.
