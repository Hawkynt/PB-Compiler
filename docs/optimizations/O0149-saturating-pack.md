# O0149 — Saturating narrowing and pack recognition

| | |
|---|---|
| **Status** | ⬜ Planned (the saturating instructions are implemented in the assembler — [R0004](R0004-asm-intrinsics.md)) |
| **Stage** | Emitter |
| **Related** | [O0148](O0148-packed-width-selection.md), [O0150](O0150-vector-compare-select.md), [R0004](R0004-asm-intrinsics.md) |

## The idea

Clamping followed by narrowing is one instruction on the SIMD units:
`PACKUSWB` packs words to unsigned bytes with saturation, `PADDUSB`/`PSUBUSB`
add and subtract with saturation. The scalar source spells it as a compare and
two assignments — the classic graphics "clamp to 0..255" — which is exactly the
shape to recognize.

## Applies to

```basic
DIM i%, src%(0 TO 999), dst(0 TO 999) AS BYTE, v%
FOR i% = 0 TO 999
  v% = src%(i%) + 40
  IF v% > 255 THEN v% = 255
  IF v% < 0 THEN v% = 0
  dst(i%) = v%
NEXT
```

## Today

Two compares and two branches per element, and no vectorization (the body
branches).

## Planned

```asm
    movq    mm0, [si]        ; four words
    paddw   mm0, mm7         ; +40 per lane
    packuswb mm0, mm0        ; clamp to 0..255 and narrow, in one instruction
    movd    [di], mm0
```

## What it needs

- An **idiom recognizer** for the clamp shape in all its spellings (two `IF`s, a
  `MIN`/`MAX` pair, a `SELECT CASE`), producing a canonical "saturating narrow"
  node the vectorizer can lower.
- Signed and unsigned variants (`PACKSSWB` vs `PACKUSWB`) chosen by the clamp
  bounds.
- Exactness: saturation must reproduce the scalar clamp for **every** input,
  including the boundary values and the negative side — which is what makes the
  recognizer's bound matching strict rather than approximate.
