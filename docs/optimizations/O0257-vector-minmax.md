# O0257 — Packed min/max

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0248](O0248-branchless-minmax.md), [O0145](O0145-vector-reduction.md), [O0119](O0119-reduction-recognition.md) |
| **Split from** | [O0150](O0150-vector-compare-select.md) |

## The idea

`PMAXSW`/`PMINSW` (and the byte/unsigned variants) compute a per-lane min or max
in one instruction. A min/max **reduction** loop then vectorizes with a packed
accumulator and one horizontal combine, exactly like a sum
([O0145](O0145-vector-reduction.md)).

## Applies to

```basic
$CPU 80586 MMX
$OPTIMIZE SPEED
DIM a%(0 TO 999), i%, m%
FOR i% = 0 TO 999
  IF a%(i%) > m% THEN m% = a%(i%)
NEXT
```

```asm
Top:
    movq    mm0, [si]
    pmaxsw  mm1, mm0         ; four running maxima
    add     si, 8
    ...
    ; then fold the four lanes
```

## What it needs

- The min/max recognizer ([O0248](O0248-branchless-minmax.md)) and the reduction
  classifier ([O0119](O0119-reduction-recognition.md)).
- Min/max is **associative and commutative**, which is what makes the lane split
  and the horizontal fold exact — the same argument as the sum reduction.
- Signed/unsigned instruction selection by element type; `PMAXSW` is signed-word
  only, so byte and unsigned forms need the SSE2 variants or a mask sequence.
