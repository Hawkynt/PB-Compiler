# O0248 — Branchless min/max

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0108](O0108-branchless-select.md), [O0119](O0119-reduction-recognition.md), [O0257](O0257-vector-minmax.md) |
| **Split from** | [O0108](O0108-branchless-select.md) |

## The idea

`IF a > b THEN m = a ELSE m = b` is a min/max, and every target has a cheaper
form than a branch: `CMP` + `CMOVcc` on a 686, `PMAXSW`/`PMINSW` in a vector
loop, and on an 8086 a mask built from the carry flag.

Recognizing min/max **by name** matters more than the generic select, because it
is the shape a reduction loop carries
([O0119](O0119-reduction-recognition.md)) and the one the packed instructions
implement directly.

## Applies to

```basic
DIM a%, b%, m%
IF a% > b% THEN m% = a% ELSE m% = b%
```

## What it needs

- A recognizer over the `IF`/`SELECT`/ternary spellings of the same idiom.
- Profitability: on an 8086 a predictable branch beats mask arithmetic, so the
  cost model decides ([O0174](O0174-target-cost-models.md)).
- Signed versus unsigned selection, and the `-32768` edge for the negated forms.
