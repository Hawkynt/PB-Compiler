# O0194 — Hot accumulator in DI

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Optimize.cs` — `FindAccumulator` |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` |
| **Verified by** | `tests/diff/DIFF52.BAS`, scenario `HotAccumulatorWinsTheRegister` |
| **Split from** | [O0005](O0005-register-residency.md) (which is now the FOR counter in SI) |

## What it is

One hot 2-byte INTEGER accumulator lives in **DI** across the loop, so its
per-iteration load and store disappear.

The register goes to the **hottest** value, not the first one seen:
`FindAccumulator` prefers a *self-referential* accumulator (`acc = acc OP …`,
`INCR`, `DECR` — a value carried across iterations) over a variable merely
assigned each pass.

## Sample

```basic
$OPTIMIZE SPEED
DIM i%, s%, scratch%
FOR i% = 1 TO 100
  scratch% = i% AND 7        ' throwaway
  s% = s% + scratch%         ' carried: this one wins DI
NEXT
```

## With the optimizer

```asm
Top:
    ...
    add     di, [bp-scratch] ; the scratch stays in memory, read as an operand
```

Parking the scratch instead would leave the hot value loading and storing every
iteration — which is what the preference rule prevents.

## Why it is safe

Residency requires a DI-clean body (no call, no inline asm, nothing that could
observe the variable's cell while DI holds the live value), and the value is
flushed to its cell on every exit path.
