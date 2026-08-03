# O0221 — 32-bit operation narrowing

| | |
|---|---|
| **Status** | ✅ Implemented (2026-08) |
| **Stage** | Emitter, on the value lattice |
| **Source** | `CodeGen/CodeGenerator.cs` — `NarrowRangeOf` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF112.BAS`, `OptimizerTests` |
| **Split from** | [O0016](O0016-value-fact-analysis.md) |

## What it is

A 32-bit **comparison** or integral **multiply** whose operands the lattice
proves both fit one 16-bit word runs on the 16-bit ALU:

- comparison — one `CMP AX,BX` plus the ordinary −1/0 materialization, instead
  of the nine-instruction `SUB`/`SBB`/`OR`/`TEST` sequence;
- multiply — one `IMUL`/`MUL BX` instead of the three-MUL `rt_lmul` call.

## Sample

```basic
DIM a&, b&, i%
a& = i% AND 255
b& = i% AND 127
IF a& > b& THEN PRINT "greater"
```

## Why it is safe

- A value inside a word **equals its own sign/zero extension**, so the high
  halves cannot change the ordering.
- `int16 * int16` and `uint16 * uint16` always fit the 32-bit result.

`NarrowRangeOf` is deliberately **stricter** than
[O0217](O0217-bounds-check-elimination.md)'s `IndexRangeOf`: *every* node of the
operand tree must fit a word, so no intermediate can have wrapped and made the
mathematical range a fiction. The distinction matters exactly when the range
**replaces** an operation rather than merely bounding it.
