# O0222 — Fact-proven identity removal

| | |
|---|---|
| **Status** | 🟡 Partial — an AND whose mask keeps every bit the operand could set is removed, and a signed remainder by a power of two of a non-negative value becomes that AND (`KnownBitsSimplify`); bits under a truncation drop out (`DemandedBits`); arbitrary fact-proven identities are not covered |
| **Stage** | IR middle end |
| **Verified by** | `KnownBitsSimplifyTests`; `DemandedBitsTests` |
| **Source** | `Ir/Passes/KnownBitsSimplify.cs`; `Ir/Passes/DemandedBits.cs` — `TryDropDiscardedBitOperation`; `Ir/Analysis/IrKnownBitsAnalysis.cs` |
| **Gate** | `--optimize` |
| **Split from** | [O0016](O0016-value-fact-analysis.md) |

## What it is

An operation whose facts prove it changes nothing is not emitted — only its
operand is:

| Operation | Proven by |
|---|---|
| `x AND 255` | the bits already show the high byte clear |
| `x OR 1` | bit 0 is already set |
| `x XOR z` | `z` is a provable zero |
| `x MOD k` | the range already lies inside `[0,k)` |

Neither side has to be a literal: the mask may be any expression whose bits are
known (`m% = n% OR 255` is not a constant, yet its low byte is provably all
ones), and the bitwise operators are symmetric, so either side may be the one
that disappears.

## Sample

```basic
DIM b AS BYTE, n%, m%
n% = b AND 255               ' b is 0..255: the AND does nothing
m% = (n% OR 255) AND 255     ' the outer AND does nothing either
```

## Why it is safe

The side that disappears must be **discardable** — a plain variable read or a
compile-time constant. Anything else could call a `FUNCTION`, or index an array
whose bounds check is part of what the program is observed to do under
`$ERROR BOUNDS`. `FactsOf` never fails: an operand nothing is known about yields
the lattice's top element rather than aborting the query.
