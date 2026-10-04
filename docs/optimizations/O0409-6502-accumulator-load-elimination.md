# O0409 — 6502 accumulator-load elimination

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | 6502 assembler, on the instruction stream before layout |
| **Source** | `Backend/Mos6502/Mos6502Assembler.cs` (`RemoveRedundantLoads`) |
| **Gate** | always, on `--platform 6502` |
| **Verified by** | `Mos6502CodeSizeTests` (one test per case it must keep), `Mos6502BatteryTests`, `FlatTargetIdiomTests` |
| **Related** | [O0034](O0034-redundant-load-elimination.md), [O0408](O0408-6502-frame-slot-sharing.md) |

## What it is

The 6502 version of [O0034](O0034-redundant-load-elimination.md), and wider: an `LDA` whose value A
already holds is dropped - the same immediate as the last `LDA #`, or the cell A was last loaded from
or stored to and that nothing has written since. Clearing a record writes `LDA #0 : STA x` per byte,
and every byte after the first loses its load.

What A holds is followed through straight-line code only. A label forgets it, since control can
arrive from anywhere; so do a call, an instruction that changes A, and a store that might be to the
remembered cell. A load also sets N and Z, so it stays wherever a branch, `PHP` or a return could read
those flags before an instruction sets them again. The KERNAL's page-zero cells and the chips'
registers are never remembered: the interrupt moves the jiffy clock between two reads of it. And the
one-byte `BIT` that [O0410](O0410-6502-branch-shaping.md) uses to step over an instruction joins two
paths without a label, which the pass honours too.

## Sample

```basic
TYPE Cell
  a AS LONG
  b AS INTEGER
END TYPE
DIM c AS Cell
c.a = 0: c.b = 0
```

## Without it

```asm
    lda #0 : sta c+0
    lda #0 : sta c+1
    lda #0 : sta c+2
    lda #0 : sta c+3
    lda #0 : sta c+4
    lda #0 : sta c+5
```

## With it

```asm
    lda #0
    sta c+0 : sta c+1 : sta c+2 : sta c+3 : sta c+4 : sta c+5
```

## Equivalent BASIC

None - the same stores happen; only the loads that changed nothing are gone.
