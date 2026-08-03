# O0020 — Algorithmic idiom replacement

| | |
|---|---|
| **Status** | ✅ Implemented (empty loop, constant fill, arithmetic series, array copy loop) |
| **Stage** | Emitter, before unrolling is considered |
| **Source** | `CodeGen/CodeGenerator.Optimize.cs` — `#region O20`, `TryEmitForIdiom` |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` |
| **Related** | [O0007](O0007-loop-unrolling.md), [O0025](O0025-pure-function-folding.md), [O0073](O0073-algorithmic-idiom-catalog.md) |
| **Split into** | [O0227](O0227-constant-fill-stosw.md), [O0228](O0228-series-folding.md), [O0229](O0229-copy-loop-movsw.md) |

## What it is

Instead of optimizing a loop instruction by instruction, the compiler recognizes
what the **whole loop computes** and substitutes a better algorithm — but only
where the result is provably bit-identical.

**This page covers the empty loop**: a constant-trip `FOR` whose body has no
statements *is* its counter's end value, stored once. The other recognized
shapes — constant fill, arithmetic series, array copy — each have their own
entry (see *Split into* above).

The counter cell always ends on the value the rolled loop would have left
(increment-then-test, 16-bit wrap included).

## Sample

```basic
$OPTIMIZE SPEED
DIM i%, s%, a%(0 TO 99)
FOR i% = 1 TO 1000 : NEXT              ' empty
FOR i% = 0 TO 99 : a%(i%) = 7 : NEXT   ' constant fill
FOR i% = 1 TO 100 : s% = s% + i% : NEXT ' arithmetic series
PRINT i%; s%
```

## Without the optimizer

Three real loops — 1 000 + 100 + 100 iterations of compare, body, increment and
back-edge.

## With the optimizer

```asm
    mov     word ptr [i], 03E9h    ; 1001: the empty loop IS its end value
    push    ds                     ; constant fill
    pop     es
    lea     di, [a]
    mov     cx, 0064h
    mov     ax, 0007h
    rep     stosw
    mov     word ptr [i], 0064h
    mov     ax, [s]                ; the series total, added once
    add     ax, 13BAh              ; 5050
    mov     [s], ax
    mov     word ptr [i], 0065h
```

## Equivalent BASIC

```basic
DIM i%, s%, a%(0 TO 99)
i% = 1001
' a%() filled with 7 by a block store
i% = 101
s% = s% + 5050
PRINT i%; s%
```

## Why it is safe

- The iterates are **simulated exactly** like the generic loop engine (signed
  compare, 16-bit wrap on increment), and a wrap-around marathon aborts the
  recognition rather than guessing.
- `$OPTIMIZE SPEED` gating is not a performance preference but a correctness
  courtesy: DOS-era code uses empty loops as **delay loops**. Any `TIMER`,
  `INP` or `PEEK` access in scope keeps the loop.
- An `$ERROR NUMERIC` overflow still raises exactly where the looped original
  would have raised it.

## Limits

MIN/MAX scans, bubble-sort shapes lowering to `ARRAY SORT`, and further whole
algorithm recognitions are [O0073](O0073-algorithmic-idiom-catalog.md).
