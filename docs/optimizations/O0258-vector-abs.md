# O0258 — Packed absolute value

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0249](O0249-branchless-abs.md), [O0150](O0150-vector-compare-select.md) |
| **Split from** | [O0150](O0150-vector-compare-select.md) |

## The idea

A loop that takes the absolute value of every element — a branch per element
today — becomes a packed sequence: on SSSE3 the `PABSW` instruction, and on
plain MMX/SSE2 the mask form `mask = PSRAW(x, 15)`, `(x XOR mask) - mask`, which
is the vector transcription of the scalar three-instruction idiom.

## Applies to

```basic
$CPU 80586 MMX
$OPTIMIZE SPEED
DIM a%(0 TO 999), i%
FOR i% = 0 TO 999
  IF a%(i%) < 0 THEN a%(i%) = -a%(i%)
NEXT
```

## What it needs

- The same recognizer as [O0249](O0249-branchless-abs.md), applied to a loop
  body rather than a statement.
- The `-32768` lane: its absolute value wraps to itself, which is what both the
  scalar branch form and the mask form produce — so the vector result stays
  byte-identical to the scalar loop.
