# O0352 — Conversion range-check elimination

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0016](O0016-value-fact-analysis.md), [O0089](O0089-extension-elimination.md), [O0013](O0013-promotion-lowering.md) |

## The idea

A narrowing conversion under `$ERROR NUMERIC` checks that the value fits the
destination. When the range already proves it does, the check — and, for a float
source, the whole x87 comparison protocol around it — is dead.

## Applies to

```basic
$ERROR NUMERIC ON
DIM n&, b AS BYTE, i%
FOR i% = 0 TO 99
  n& = i%
  b = n&                     ' [0,99] fits a BYTE: the check cannot fire
NEXT
```

## What it needs

- The interval domain ([O0016](O0016-value-fact-analysis.md)) at the conversion
  site — the same query [O0217](O0217-bounds-check-elimination.md) makes for
  subscripts.
- The float-to-integer case is the valuable one and the harder one: the check is
  a comparison plus a status-word round trip, and the proof needs the float fact
  domain ([O0346](O0346-fp-classification-simplification.md)) rather than the
  integer lattice.
- A check that *could* fire is never dropped — the error is observable
  behaviour.
