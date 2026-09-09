# O0352 — Conversion range-check elimination

| | |
|---|---|
| **Status** | ✅ Implemented (bounded non-NaN float provenance) |
| **Stage** | Emitter |
| **IR** | `PowerBasic.Compiler/Ir/Passes/ConversionRangeCheckElim.cs` |
| **Related** | [O0016](O0016-value-fact-analysis.md), [O0089](O0089-extension-elimination.md), [O0013](O0013-promotion-lowering.md) |

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

The existing integer `IrRangeAnalysis` intentionally refuses floating comparisons because an arbitrary
floating value may be NaN. `ConversionRangeCheckElim` adds a narrow provenance domain instead of
weakening that safety rule: floating constants and values produced by `SIToFP`/`UIToFP` from a bounded
integer SSA value are known non-NaN and get numeric endpoints. `FPExt`, phis and selects composed only
from such values preserve the fact. Ordered comparisons at the conversion guard can then be folded
only when every value in both intervals gives the same result.

An arbitrary float argument, a NaN constant, or an unmodelled floating computation remains unknown and
keeps its check. General float classification/range reasoning remains the domain of
[O0346](O0346-fp-classification-simplification.md).

## What it needs

- The interval domain ([O0016](O0016-value-fact-analysis.md)) at the conversion
  site — the same query [O0217](O0217-bounds-check-elimination.md) makes for
  subscripts.
- A no-NaN proof before ordered floating comparisons can be decided. The IR port obtains that proof
  from conversion provenance rather than assuming it for arbitrary floats.
- A check that *could* fire is never dropped — the error is observable behaviour.
