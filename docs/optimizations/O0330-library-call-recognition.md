# O0330 — Library call recognition

| | |
|---|---|
| **Status** | ⬜ Planned (the fill and copy shapes are done — [O0227](O0227-constant-fill-stosw.md), [O0229](O0229-copy-loop-movsw.md)) |
| **Stage** | Mid-end |
| **Related** | [O0020](O0020-idiom-replacement.md), [O0073](O0073-algorithmic-idiom-catalog.md), [O0339](O0339-memory-routine-by-size.md) |

## The idea

Hand-written loops that reimplement a runtime primitive are replaced by the
primitive: `memcpy`, `memset`, `memcmp`, `strlen`, a search, a math routine. The
runtime version is written once, tuned once
([R0003](R0003-string-engine.md)), and widened per target
([O0241](O0241-dword-string-copy.md)) — which no open-coded loop will ever be.

Two of these already exist as idiom replacements; what is missing is the general
recognizer and the rest of the catalog.

## Applies to

```basic
DIM i%, n%, a$(0 TO 99)
' hand-written length scan over a fixed buffer
i% = 1
DO WHILE MID$(buf$, i%, 1) <> CHR$(0)
  i% = i% + 1
LOOP                         ' this is strlen
```

## What it needs

- A pattern set over loop shapes, with an **exactness proof per pattern**: the
  primitive must agree with the loop on every input, including the empty case,
  the overlapping case (`memmove` vs `memcpy`) and the trap behaviour under
  `$ERROR BOUNDS`.
- The `$OPTIMIZE SPEED` gate and the delay-loop caution from
  [O0020](O0020-idiom-replacement.md).
