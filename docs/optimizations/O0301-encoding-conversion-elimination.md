# O0301 — Encoding-conversion elimination

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0286](O0286-allocation-elimination.md), [O0089](O0089-extension-elimination.md), [O0303](O0303-formatted-print-specialization.md) |

## The idea

Back-to-back conversions that cancel out should not happen, and a value should be
kept in the **representation its consumers want**. In PB the "encodings" are the
string/number boundary and the fixed/dynamic/ASCIIZ string forms:

| Pattern | Result |
|---|---|
| `VAL(STR$(n))` | `n` |
| `STR$(VAL(s$))` | `s$` only if the format round-trips — usually **not** |
| ASCIIZ → dynamic → ASCIIZ | one copy, or none |
| `CHR$(ASC(s$))` | `LEFT$(s$, 1)` |

## Applies to

```basic
DIM n&, t$
t$ = STR$(n&)
PRINT VAL(t$)                ' prints n& — the round trip is dead
```

## What it needs

- Exact knowledge of which conversions are **lossless in which direction**.
  `VAL(STR$(n))` is exact for integers; `STR$(VAL(s$))` is not, because `STR$`
  normalizes spacing and precision — folding it would change output.
- The same care around `$COMPAT` and dialect-specific number formatting, where
  the string form differs between dialects (`docs/QUIRKS.md`).
