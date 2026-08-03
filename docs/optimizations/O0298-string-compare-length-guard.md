# O0298 — String comparison length guard

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Runtime + emitter |
| **Related** | [O0181](O0181-empty-string-comparison.md), [O0180](O0180-string-length-caching.md), [R0003](R0003-string-engine.md) |

## The idea

For `=` and `<>`, two strings of **different lengths** are unequal — no byte
needs to be examined. Testing lengths first turns the common negative case into
two loads and a compare, and the positive case can then run a widened content
comparison (`REPE CMPSW`/`CMPSD`) since the lengths are known equal.

Ordering comparisons (`<`, `>`) still need the content, but can compare the
common prefix wide and only then consider the length difference.

## Applies to

```basic
DIM a$, b$
IF a$ = b$ THEN ...
```

## What it needs

- The length guard in `StrCmp` itself — one implementation benefiting every
  program, rather than a codegen pattern.
- Widened content comparison with a tail
  ([O0241](O0241-dword-string-copy.md) does the same for copying).
- PB's exact comparison semantics for the ordering forms, including how a
  shorter string that is a prefix of a longer one orders.
