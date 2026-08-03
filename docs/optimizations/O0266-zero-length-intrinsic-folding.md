# O0266 — Zero-length string intrinsic folding

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0178](O0178-empty-string-simplification.md), [O0001](O0001-constant-folding.md), [O0180](O0180-string-length-caching.md) |
| **Split from** | [O0178](O0178-empty-string-simplification.md) |

## The idea

String intrinsics with a provably zero length produce the empty string and need
no runtime call at all:

| Expression | Result |
|---|---|
| `LEFT$(s$, 0)`, `RIGHT$(s$, 0)`, `MID$(s$, i, 0)` | `""` |
| `SPACE$(0)`, `STRING$(0, c)` | `""` |
| `MID$(s$, i)` where `i > LEN(s$)` is provable | `""` |

## Applies to

```basic
DIM s$, t$, n%
n% = 0
t$ = LEFT$(s$, n%)           ' n% is provably 0
```

## What it needs

- The length argument's range from [O0016](O0016-value-fact-analysis.md) — a
  literal zero is the easy case, a provably-zero variable the useful one.
- The result must still be a **valid empty string value** in the target's
  representation (handle 0 or a zero-length descriptor), so it composes with
  [O0181](O0181-empty-string-comparison.md)'s representation invariant.
- The source string is still evaluated if it could have an effect.
