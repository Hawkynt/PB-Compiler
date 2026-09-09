# O0297 — Substring as a view

| | |
|---|---|
| **Status** | 🟡 Partial (single-character reads/compares and `LEN(LEFT$/RIGHT$/MID$)` are allocation-free; the general compare/print view is not) |
| **Stage** | Mid-end + emitter |
| **IR** | ✅ `Ir/Passes/StringByteRead.cs` (`strbyte`) covers the single-character case; `Ir/Passes/StringSliceLength.cs` (`strslice`) covers immediately measured general slices. `ASC(MID$(s$, i, 1))` lowers to a substring allocation whose only reader is `ASC`, so the heap is entered and left again for one byte; `rt_charat` reads it out of the source buffer. The two agree at every boundary - both clamp a start below 1 to 1, both answer 0 past the end, both consume the handle - and the length must be the CONSTANT 1, since proving a runtime length equal to 1 is SCCP's job. `LEN(LEFT$/RIGHT$/MID$)` instead consumes the original handle with `rt_str_len` at the slice producer site and derives the clamped slice length in SSA, removing the allocation/copy without extending the handle lifetime |
| **Related** | [O0286](O0286-allocation-elimination.md), [O0293](O0293-copy-on-write-elision.md), [O0298](O0298-string-compare-length-guard.md) |

## The idea

`LEFT$`, `RIGHT$` and `MID$` allocate a copy. When the result is **temporary and
read-only** — compared, measured, printed, or scanned — a pointer-and-length
*view* into the original storage does the same job with no allocation and no
copy.

## Applies to

```basic
DIM s$
IF MID$(s$, 5, 3) = "abc" THEN ...     ' allocates 3 bytes to compare 3 bytes
PRINT LEFT$(s$, 10)                    ' allocates 10 bytes to print them
n% = LEN(MID$(s$, i%, count%))         ' only needs the clamped slice length
```

## Now

The most common **single-character** view ships: `ASC(MID$(s$, i, 1))`,
`ASC(LEFT$(s$, 1))` (the first character), `ASC(RIGHT$(s$, 1))` (the last, via
`rt_lastchar`), and the two-argument `ASC(s$, i)` — a one-character substring
immediately consumed by `ASC` — read the byte straight from the source buffer
(`rt_charat`/`rt_lastchar`) instead of allocating a one-character string and reading
its head. In a character-scan loop
that removes one heap allocation *per character*. It reproduces `MID$`'s edge
behaviour exactly (start clamps to 1, a start past the end yields 0), and consumes
(frees) the operand like the substring path it replaces. `rt_charat` lives in its
own trimmed section referenced only by the optimized emitter, so the faithful build
keeps the substring form byte-for-byte (golden gate 250/250). Verified by a
self-differential DOSBox run over every edge — `i` in range, `i < 1` (clamped),
`i > LEN`, an empty string, first and last character — identical to `$OPTIMIZE OFF`.

The **single-character compare** view ships too: `MID$(s$, i, 1) = "c"` (and
`LEFT$(s$, 1) = "c"`, `RIGHT$(s$, 1) = "c"`) / `<> "c"` — character matching in
parsers, the most common substring compare — reads the byte with `rt_charat` /
`rt_lastchar` and compares it to the literal's byte, with *no* substring, `StrDup`,
literal, or `StrCmp` allocation — three heap operations become one byte read and a
compare. Exact for a single **non-NUL** character comparand — a one-char string literal *or*
`CHR$(const)` (`MID$(s$, i, 1) = CHR$(13)`) — because a past-the-end `MID$` is `""`
whose byte reads as 0, which a non-zero byte never matches; a zero byte (`CHR$(0)` /
a NUL literal) is excluded because it would alias that 0. Operands in either order. Verified by a
self-differential DOSBox run over a match, a mismatch, a clamped and a past-the-end
index, an empty string, and the swapped-operand and `<>` spellings — all identical to
`$OPTIMIZE OFF`.

The **measured general slice** now ships in the IR as well. When a `LEFT$`, `RIGHT$`
or `MID$` temporary has exactly one consumer and that consumer is `LEN`,
`StringSliceLength` replaces the substring producer with `rt_str_len` on the original
owned handle at the **same instruction position**, then computes the result with
signed clamp/select arithmetic:

- `LEFT$` / `RIGHT$`: `min(max(n, 0), sourceLength)`;
- `MID$(s$, start, n)`: `min(max(n, 0), max(sourceLength - max(start, 1) + 1, 0))`;
- `MID$(s$, start)`: `max(sourceLength - max(start, 1) + 1, 0)`.

That removes the allocation and byte copy even when start/count are runtime values,
while preserving their evaluation and the original handle-consumption point. The
single-user restriction is the lifetime rule in executable form: a slice that still
has another reader remains a real string.

## Still planned

- The general read-only view for longer `LEFT$`/`RIGHT$`/`MID$` results that are
  compared (compose with [O0298](O0298-string-compare-length-guard.md)) or printed —
  a segment/offset/length view the emitter passes without entering the string manager,
  with the expression-local lifetime rule below.

## What it needs

- A **view representation** the emitter can pass to the remaining comparison and print
  paths without entering the string manager — segment, offset, length. The LEN consumer
  no longer needs one because the middle end can derive its answer directly.
- A lifetime rule: the view is only valid while the base string is unchanged and
  unmoved, so any allocation (which can compact the heap) between creation and use
  invalidates it. That makes views strictly *expression-local* unless
  [O0260](O0260-escape-analysis.md) proves more.
- The fallback stays the real substring, so nothing is lost where the view is
  illegal.
