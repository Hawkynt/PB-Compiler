# O0298 — String comparison length guard

| | |
|---|---|
| **Status** | 🟡 Partial (equality `=` / `<>` short-circuit on length; the widened-content compare for ordering forms is not done) |
| **Stage** | Runtime + emitter |
| **Related** | [O0181](O0181-empty-string-comparison.md), [O0180](O0180-string-length-caching.md), [R0003](R0003-string-engine.md) |

## Now

For `=` and `<>`, two strings of different lengths are unequal without examining a
byte. A dedicated runtime routine `rt_strcmpeq` (`EmitStrCmpEq`, `DosRuntime.Strings.cs`)
loads both descriptors and, when the lengths differ, returns "unequal" immediately —
turning the common negative case into two loads and a compare, where the full
`rt_strcmp` still `REPE CMPSB`s the common prefix before comparing lengths. The
emitter routes a `=` / `<>` string comparison to it under `--optimize`
(`CodeGenerator.Expressions.cs`); it returns 0 (equal) / 1 (unequal), which the same
`je`/`jne` test reads, and consumes (frees) both operands exactly like `rt_strcmp`.

`rt_strcmpeq` lives in its **own trimmed section**, referenced only by the optimized
emitter, so the faithful build keeps the full three-way compare byte-for-byte (golden
gate 250/250). Verified by a self-differential DOSBox run over equal, unequal
same-length, unequal different-length (the guard path), prefix (`"hello"` vs
`"hello world"`), empty and literal comparisons — all identical to `$OPTIMIZE OFF` —
plus a regression test that the `=` routine begins with the length guard while an
ordering `<` keeps the min computation.

## Still planned

- The **widened content comparison** (`REPE CMPSW`/`CMPSD` with a tail) for the
  equal-length case, and the ordering forms `<` / `>` comparing the common prefix
  wide before considering the length difference.

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
