# O0352 — Conversion range-check elimination

| | |
|---|---|
| **Status** | ✅ Implemented (shared finite/non-NaN FP domain) |
| **Stage** | IR middle end |
| **IR** | `PowerBasic.Compiler/Ir/Passes/ConversionRangeCheckElim.cs` |
| **Related** | [O0016](O0016-value-fact-analysis.md), [O0089](O0089-extension-elimination.md), [O0346](O0346-fp-classification-simplification.md) |

## The idea

A narrowing conversion whose error mode checks that the value fits the destination does not need the
check when the value range already proves it fits. For a float source this can remove the complete
ordered-comparison protocol around the conversion.

The current IR lowering arms these conversion guards under `$ERROR OVERFLOW ON`; `$ERROR NUMERIC ON`
currently guards the FOR-counter wrap only. The older catalogue wording used NUMERIC for this case,
so the implementation follows the compiler's actual observable semantics rather than reproducing that
stale label.

## Applies to

```basic
$ERROR OVERFLOW ON
DIM b AS BYTE, i%
FOR i% = 0 TO 99
  b = i% * 1.0              ' the converted value stays in [0,99]
NEXT
```

## IR implementation

`ConversionRangeCheckElim` consumes the shared `FpDomainAnalysis` instead of maintaining a second float
range evaluator. That analysis adapts `IrRangeAnalysis` facts through `SIToFP`/`UIToFP`, supported
floating add/subtract/multiply/divide expressions, `FPExt`, and `FPTrunc`, reproducing binary32 or
binary64 rounding at the IR operation where it occurs. A conversion guard comparison is folded only
when both operands have finite, non-NaN domains and every value in those closed intervals gives the
same ordered-comparison result.

O0352 retains conservative phi/select joins around those proven domains so a merge of individually
safe conversion inputs remains safe. Cyclic or excessively deep joins are declined rather than guessed.
Extended-precision expressions that the shared analysis cannot evaluate host-independently remain
unknown.

An arbitrary float argument, a NaN/infinity-bearing domain, or an unmodelled floating computation keeps
its check. General float classification/range reasoning remains shared with
[O0346](O0346-fp-classification-simplification.md), rather than being duplicated here.

## What it needs

- The interval domain ([O0016](O0016-value-fact-analysis.md)) at the conversion site — the same query
  [O0217](O0217-bounds-check-elimination.md) makes for subscripts.
- A finite, non-NaN proof before an ordered floating comparison can be decided.
- Precision-aware endpoint evaluation for floating arithmetic; host-double algebra is not accepted as
  a substitute for binary32/binary64 IR rounding.
- A check that *could* fire is never dropped — the error is observable behaviour.
