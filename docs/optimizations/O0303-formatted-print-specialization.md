# O0303 — Formatted-print specialization

| | |
|---|---|
| **Status** | 🟡 Partial (constant IR fields become pooled literal output; dynamic fields still use `rt_usefmt`) |
| **Stage** | Emitter |
| **IR** | `PowerBasic.Compiler/Ir/Passes/FormattedPrintSpecialization.cs` |
| **Related** | [P0007](P0007-trivial-io-lowering.md), [P0001](P0001-runtime-trimming.md), [R0003](R0003-string-engine.md) |

## The idea

`PRINT USING` and `PRINT` with mixed operands go through formatting helpers.
When the format is a **literal**, interpreting its field structure is compile-time
work: emit a straight-line sequence of the specific conversions the format calls
for, and avoid the general formatter wherever the value is known too.

[P0007](P0007-trivial-io-lowering.md) already does the extreme case — a program
whose whole output is known — by precomputing the bytes. O0303 works at a smaller
granularity, so a constant formatted field can disappear even when the surrounding
statement or program still contains dynamic output.

## Applies to

```basic
DIM n%, s$
PRINT USING "###.##"; x!
PRINT "n="; n%; " s="; s$
```

## IR implementation

Literal `PRINT USING` formats are already parsed by `Runtime.UsingFormat` during
IR lowering. Each numeric field reaches the IR as a scaled, ties-to-even rounded
`i32` plus the packed width/decimal/grouping specification consumed by
`rt_usefmt`; literal runs are already ordinary `rt_print_str` calls.

`FormattedPrintSpecialization` runs in the existing late `strfold` module phase,
after SCCP and the other function value passes have exposed constants. When both
the scaled field value and packed specification are constant, it renders the DOS
runtime's current fixed-point contract at compile time and replaces
`rt_using_field` with a pooled `rt_print_str` literal. The file form preserves its
file-number operand and becomes `rt_fprint_str`, so file selection remains where
the original call performed it. LPRINT and USING$ continue to work through the
same ordinary print routing/capture mechanism.

The renderer deliberately mirrors the runtime surface rather than genuine PB
features the runtime does not implement: right alignment, sign inside the field,
optional thousands grouping, decimal zero-fill, and non-truncating field overflow.
The value has already been rounded and decimal-scaled before this pass sees it.

A field whose value is still dynamic is left on `rt_usefmt`. Consequently the
formatter can disappear from a linked image only when all remaining USING fields
were specialized; full dynamic-field straight-line lowering remains open.

## Validation contract

The middle-end regression tests pin the formatter-sensitive cases independently:
fractional alignment, negative values, thousands grouping, leading fractional
zeroes, values wider than their fields, the full signed-32-bit magnitude, file
routing, and the dynamic fallback. The existing backend `PRINT USING` oracle tests
continue to cover routed-vs-direct output including x87 ties-to-even rounding,
printer output and USING$ capture.
