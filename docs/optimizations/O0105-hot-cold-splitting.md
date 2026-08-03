# O0105 — Hot/cold block splitting

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Assembler layout |
| **Related** | [O0104](O0104-block-placement.md), [O0106](O0106-trace-formation.md), [P0006](P0006-header-squeeze.md) |

## The idea

Error handling, diagnostics and rarely-taken arms are moved out of the hot
instruction stream entirely — to the end of the procedure, or to a separate cold
region of the image. The hot code then occupies fewer cache lines (or, on an
8086, fewer prefetch-queue refills and fewer bytes between the instructions that
actually run).

## Applies to

```basic
DIM i%, a%(0 TO 999)
FOR i% = 0 TO 999
  IF a%(i%) < 0 THEN
    PRINT "negative element at"; i%      ' cold: emitted inside the loop today
    PRINT "aborting"
    EXIT FOR
  END IF
  a%(i%) = a%(i%) * 2
NEXT
```

## Today

The two `PRINT`s and their string references sit between the test and the loop
body, so every iteration fetches past them.

## Planned

The cold arm is relocated behind the procedure; the loop body becomes a compact
contiguous run.

## What it needs

- The edge probabilities from [O0104](O0104-block-placement.md) — splitting is
  the layout consequence of the inference, not a separate analysis.
- A region-relocation facility in the assembler; the region bookkeeping that
  [O0040](O0040-identical-code-folding.md) uses for folding is the same
  machinery.
- Care with short-form branches: a relocated arm may exceed the ±127-byte range,
  so relaxation must run after the move
  ([O0035](O0035-jump-relaxation.md)).
