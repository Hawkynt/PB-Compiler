# O0228 — Arithmetic-series folding

| | |
|---|---|
| **Status** | ✅ Implemented (constant bounds) |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Optimize.cs` — `TryEmitForIdiom` |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` |
| **Split from** | [O0020](O0020-idiom-replacement.md) |

## What it is

A constant-trip loop whose body accumulates the counter (`s = s + i`) computes a
closed-form total. The loop is replaced by a single add of the folded sum.

## Sample

```basic
$OPTIMIZE SPEED
DIM i%, s%
FOR i% = 1 TO 100
  s% = s% + i%
NEXT
```

## Without / with

```asm
    ; without: 100 iterations

    mov     ax, [s]          ; with
    add     ax, 13BAh        ; 5050, folded at compile time
    mov     [s], ax
    mov     word ptr [i], 0065h
```

## Equivalent BASIC

```basic
s% = s% + 5050
i% = 101
```

## Why it is safe

The total is computed by **simulating the exact iterates** (signed compare,
16-bit wrap on increment), not by the textbook `n(n+1)/2` formula — so the
wrap behavior of the accumulated value is reproduced rather than assumed. An
`$ERROR NUMERIC` overflow still raises exactly where the looped original would.

## Limits

Variable bounds need the symbolic closed form and its overflow proof —
[O0134](O0134-recurrence-shortening.md).
