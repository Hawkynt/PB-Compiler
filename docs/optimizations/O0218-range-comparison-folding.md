# O0218 — Range-invariant comparison folding

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter, on the value lattice |
| **Source** | `CodeGen/CodeGenerator.cs` — `TryEmitRangeComparison`, `FoldRangeCompare` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF78.BAS` |
| **Split from** | [O0016](O0016-value-fact-analysis.md) |

## What it is

A signed comparison against a constant whose answer is **invariant over the
proven range** folds to that constant boolean — in ordinary code, not only in a
branch condition.

It also folds equalities that **no domain** can satisfy: `(x \ 2) * 2 = 1` is
impossible by the known bits (the low bit is always 0), `x AND 12 = 5` by the
bits, and `x * 10 = 25` by the congruence.

## Sample

```basic
DIM i%, f%
FOR i% = 0 TO 99
  f% = (i% < 1000)           ' always TRUE over [0,99]
  IF (i% \ 2) * 2 = 1 THEN PRINT "impossible"
NEXT
```

## With the optimizer

```asm
    mov     ax, 0FFFFh       ; the comparison folded
    mov     [f], ax
    ; the impossible IF is not emitted at all (O0002)
```

## Why it is safe

The fold requires the result to be the same for **every** value in the range —
an over-approximated range is therefore still sound, since a wider range can only
make the invariance harder to prove. `ValueFacts.Allows(candidate)` folds an
equality as soon as any one domain excludes the value.
