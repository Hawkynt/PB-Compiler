# O0343 — Transcendental function specialization

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter + runtime |
| **Related** | [O0337](O0337-polynomial-evaluation.md), [O0332](O0332-lookup-table-generation.md), [O0341](O0341-reciprocal-approximation.md) |

## The idea

`SIN`, `COS`, `EXP`, `LOG` and `ATN` are computed to full precision by the x87
or by a runtime routine. When the compiler knows the **argument range** or the
**required precision**, a cheaper approximation is exact enough:

- an angle already reduced to `0..2π` skips the range reduction;
- a result immediately truncated to an integer needs only that many bits;
- a small domain becomes a table ([O0332](O0332-lookup-table-generation.md)).

The classic demo-effect case — `INT(SIN(a) * 256)` in a loop — needs about nine
bits of the result.

## Applies to

```basic
DIM a%, s%
FOR a% = 0 TO 359
  s% = INT(SIN(a% * 3.14159 / 180) * 256)
NEXT
```

## What it needs

- The **required-precision** analysis: how many bits of the result any consumer
  observes ([O0090](O0090-demanded-bits.md) is the integer form of the same
  question).
- Fast-math gating wherever the approximation is not bit-identical to the x87
  result, which is nearly always — so on the oracle-verified dialects this is a
  fast-math feature, and on `pb36` an opt-in.
