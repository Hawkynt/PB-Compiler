# O0073 — Wider algorithmic idiom catalog

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter (extends [O0020](O0020-idiom-replacement.md)) |
| **Related** | [O0020](O0020-idiom-replacement.md), [O0026](O0026-auto-vectorization.md), [O0030](O0030-induction-variable-strength-reduction.md) |

## The idea

[O0020](O0020-idiom-replacement.md) recognizes four whole-loop shapes (empty
body, constant fill, arithmetic series, array copy). The catalog can grow, and
each addition replaces a loop with something the runtime already does better:

| Idiom | Would become |
|---|---|
| MIN/MAX scan `IF a(i) > m THEN m = a(i)` | a tight scan with the element pointer and accumulator resident, no reload |
| bubble/insertion sort shapes | `ARRAY SORT` (already a verified runtime primitive) |
| linear search `IF a(i) = k THEN found = i : EXIT FOR` | `ARRAY SCAN` |
| `s$ = s$ + CHR$(x)` build loops | a single pre-sized buffer fill |
| geometric series / power accumulation | closed form where exact |

## Applies to

```basic
$OPTIMIZE SPEED
DIM a%(0 TO 999), i%, j%, t%
FOR i% = 0 TO 998
  FOR j% = 0 TO 998 - i%
    IF a%(j%) > a%(j%+1) THEN t% = a%(j%) : a%(j%) = a%(j%+1) : a%(j%+1) = t%
  NEXT
NEXT
```

## Today

A real O(n²) bubble sort — roughly half a million compares.

## Planned

```asm
    ; the recognized shape lowers to the runtime primitive
    lea     bx, [a]
    mov     cx, 03E8h
    call    rt_array_sort
```

## Equivalent BASIC

```basic
ARRAY SORT a%(0)
```

## What it needs

- A pattern language for whole-loop recognition that is precise enough to be
  safe: every recognizer must prove the replacement is **bit-identical**,
  including for pathological inputs (equal elements, wrap-around indices,
  zero-trip loops).
- The `$OPTIMIZE SPEED` gate and the delay-loop caution from
  [O0020](O0020-idiom-replacement.md) apply unchanged.
- Sorting is the sharpest case: `ARRAY SORT`'s ordering must match the
  hand-written comparison exactly, including for unsigned element types where
  the runtime widens through the x87.
