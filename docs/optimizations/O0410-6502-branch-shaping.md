# O0410 — 6502 branch shaping

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | 6502 back end, instruction selection |
| **Source** | `Backend/Mos6502/Mos6502Compiler.Function.cs` (`LowerConditionalBranch`, `LowerCompare`) |
| **Gate** | always, on `--platform 6502` |
| **Verified by** | `Mos6502CodeSizeTests`, `Mos6502ProgramTests`, `Mos6502BatteryTests`, `FlatTargetIdiomTests` |
| **Related** | [O0031](O0031-branch-fusion.md), [O0035](O0035-jump-relaxation.md), [O0409](O0409-6502-accumulator-load-elimination.md) |

## What it is

Two shapes of the same waste: a jump stepping over code that a branch could have skipped.

**A conditional branch whose true side comes next** is emitted as its complement. Branching to the
true block and jumping to the false one costs a branch over a three-byte `JMP` when the true block is
the very next one; testing the opposite condition and branching to the false block lets the true one
follow by falling through - one two-byte branch. The complement of every integer predicate is exact,
and the soft float has no NaN, so a float comparison's is too.

**A comparison kept as a value** - stored, combined with `AND`, passed on - is materialized as 0 or 1.
Instead of `LDA #0 : JMP done : true: LDA #1 : done:`, the false path loads 0 and steps over the
`LDA #1` with the one byte `$2C`: `BIT absolute`, which takes the two bytes of `LDA #1` as its operand
and does nothing but read a byte of the stack page. Two bytes saved per comparison.

## Sample

```basic
FOR i% = -2 TO 2
  a = (i% > 0)
  IF i% > 0 THEN PRINT "p"; ELSE PRINT "n";
NEXT
```

## Without it

```asm
    ...compare...          ; branches to true when i% > 0
    jmp else
true:
    ...
    ...compare...
    lda #0
    jmp done
holds:
    lda #1
done:
    sta a
```

## With it

```asm
    ...complement...       ; branches to else when i% <= 0, falls into true
true:
    ...
    ...compare...
    lda #0
    .byte $2c              ; BIT $01A9 - steps over the next two bytes
holds:
    lda #1
    sta a
```

## Equivalent BASIC

None - the decisions are the same; only how the code reaches them changed.
