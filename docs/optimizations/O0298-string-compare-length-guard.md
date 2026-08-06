# O0298 — String comparison length guard

| | |
|---|---|
| **Status** | 🟡 Partial (equality `=` / `<>` short-circuit on length **and** widened content compare; the ordering forms `<` / `>` still compare byte-wise) |
| **Stage** | Runtime + emitter |
| **Related** | [O0181](O0181-empty-string-comparison.md), [O0180](O0180-string-length-caching.md), [R0003](R0003-string-engine.md) |

## Now

For `=` and `<>`, two strings of different lengths are unequal without examining a
byte. A dedicated runtime routine `rt_strcmpeq` (`EmitStrCmpEq`, `DosRuntime.Strings.cs`)
loads both descriptors and, when the lengths differ, returns "unequal" immediately —
turning the common negative case into two loads and a compare, where the full
`rt_strcmp` still `REPE CMPSB`s the common prefix before comparing lengths. The
emitter routes a `=` / `<>` string comparison to it under `--optimize`
(`CodeGenerator.Expressions.cs`), and likewise an equality `SELECT CASE` arm over a
string subject (`CASE "quit"`, in `EmitSelectorString`); it returns 0 (equal) /
1 (unequal), which the same `je`/`jne` test reads, and consumes (frees) both operands
exactly like `rt_strcmp`. Ordering arms (`CASE IS < …`) keep the full compare.

Once the lengths are known equal the content scan runs a **word at a time**: `SHR CX,1`
words through `REPE CMPSW`, then the single trailing byte when the length is odd. That is
half the REPE iterations of the byte scan, and it touches exactly `length` bytes — the
`length >> 1` words plus the odd byte — so a string ending at the last byte of the heap is
never read past. Widening is sound **only for equality**: `CMPSW` compares little-endian
16-bit values, so on a mismatch its sign says which word is larger as a number rather than
which string sorts first (`"ba"` is 0x6162 and `"ab"` is 0x6261, ordering them backwards).
The ordering forms therefore keep `CMPSB`.

`rt_strcmpeq` is referenced only by the optimized emitter, so the faithful build keeps the
full three-way compare for every comparison it makes (golden gate 250/250). Note it is not
*absent* from that image, though: dead-code trimming is a Tier 3 pass that runs under
`--optimize` only, so a `--dialect pb35` build carries the routine's bytes as unreferenced
dead code — measured, after this page previously claimed a "trimmed section". What the
faithful build keeps is the **call**, not the absence of the callee. Verified by a self-differential DOSBox run over equal, unequal
same-length, unequal different-length (the guard path), prefix (`"hello"` vs
`"hello world"`), empty and literal comparisons — all identical to `$OPTIMIZE OFF` —
plus a regression test that the `=` routine begins with the length guard while an
ordering `<` keeps the min computation.

## Still planned

- The ordering forms `<` / `>` comparing the common prefix wide before considering
  the length difference. This needs more than swapping the instruction: on a
  `CMPSW` mismatch the two bytes of the differing word must be re-compared to
  recover the lexicographic answer, since the word compare's own sign is the
  little-endian numeric one.
- `CMPSD` for the equal-length case on a 386 target, halving the iterations again
  behind the `$CPU` gate.

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
