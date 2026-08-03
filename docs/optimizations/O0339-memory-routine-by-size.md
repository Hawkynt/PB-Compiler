# O0339 — Memory routine specialization by size

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0330](O0330-library-call-recognition.md), [O0242](O0242-movsd-block-copy.md), [O0174](O0174-target-cost-models.md) |

## The idea

One copy routine is wrong for every size. Three regimes:

| Size | Best form |
|---|---|
| tiny (≤ 8 bytes), known | inline `MOV`s — no loop, no setup |
| medium, known | an unrolled or widened `REP MOVSW`/`MOVSD` |
| large or unknown | the runtime routine, with its alignment handling |

The block-move widening ([O0242](O0242-movsd-block-copy.md)) already covers the
middle case; the ends are missing. A 4-byte `TYPE` copy currently pays a full
`REP MOVSW` setup — load SI, DI, CX, direction — to move two words.

## Applies to

```basic
TYPE Point
  x AS INTEGER
  y AS INTEGER
END TYPE
DIM a AS Point, b AS Point
b = a                        ' 4 bytes: two MOVs beat REP MOVSW
```

## What it needs

- A size threshold table per target ([O0174](O0174-target-cost-models.md)) — the
  `REP` setup cost differs sharply between an 8086 and a 486.
- Register pressure awareness for the inline form: it needs a scratch register
  per word, which competes with residency.
