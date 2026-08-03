# O0328 — Temporary array elimination by fusion

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0062](O0062-loop-restructuring.md), [O0329](O0329-array-contraction.md), [O0286](O0286-allocation-elimination.md) |

## The idea

A producer loop that fills an intermediate array, followed by a consumer loop
that reads it once, does not need the array at all: fusing the two loops lets
each value flow **in a register** from producer to consumer, and the temporary
disappears — allocation, traffic and all.

## Applies to

```basic
DIM i%, tmp%(0 TO 999), src%(0 TO 999), out%(0 TO 999)
FOR i% = 0 TO 999 : tmp%(i%) = src%(i%) * 2 : NEXT
FOR i% = 0 TO 999 : out%(i%) = tmp%(i%) + 1 : NEXT
```

becomes

```basic
FOR i% = 0 TO 999 : out%(i%) = src%(i%) * 2 + 1 : NEXT
```

— 1 000 stores and 1 000 loads removed, plus the array itself.

## What it needs

- Loop fusion ([O0062](O0062-loop-restructuring.md)) with a dependence proof
  ([O0172](O0172-loop-dependence-analysis.md)): each element must be produced
  before it is consumed and consumed exactly once.
- The temporary must not be **read afterwards** — otherwise it still has to
  exist, and the transform degrades to
  [O0329](O0329-array-contraction.md).
- On a 64 KiB-segment target, deleting a 2 KB temporary is also a data-segment
  saving, not only a speed one.
