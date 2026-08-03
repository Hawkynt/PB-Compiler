# O0150 — Vector compare, select, min/max and absolute value

| | |
|---|---|
| **Status** | ⬜ Planned (the packed compares are implemented in the assembler — [R0004](R0004-asm-intrinsics.md)) |
| **Stage** | Emitter |
| **Related** | [O0026](O0026-auto-vectorization.md), [O0108](O0108-branchless-select.md), [O0119](O0119-reduction-recognition.md) |
| **Split into** | [O0256](O0256-vector-blend-select.md), [O0257](O0257-vector-minmax.md), [O0258](O0258-vector-abs.md) |

## The idea

A loop body with a per-element conditional does not vectorize as a branch — it
vectorizes as a **mask**:

| Scalar shape | Vector form |
|---|---|
| `IF a(i) > b(i) THEN c(i) = a(i) ELSE c(i) = b(i)` | `PCMPGTW` + `PAND`/`PANDN`/`POR`, or `PMAXSW` |
| `IF a(i) < 0 THEN a(i) = -a(i)` | packed absolute value |
| `IF a(i) = k THEN n = n + 1` | `PCMPEQW` + mask extraction + population count |

Recognizing min/max and absolute value directly is worth more than the generic
blend, because the packed instructions exist for exactly those.

## Applies to

```basic
$CPU 80586 MMX
$OPTIMIZE SPEED
DIM i%, a%(0 TO 999), b%(0 TO 999), c%(0 TO 999)
FOR i% = 0 TO 999
  IF a%(i%) > b%(i%) THEN c%(i%) = a%(i%) ELSE c%(i%) = b%(i%)
NEXT
```

## Today

Not vectorized — the body branches, so the recognizer rejects it.

## Planned

```asm
    movq    mm0, [si]        ; a
    movq    mm1, [di]        ; b
    movq    mm2, mm0
    pcmpgtw mm2, mm1         ; lane mask: a > b
    pand    mm0, mm2
    pandn   mm2, mm1
    por     mm0, mm2         ; select per lane
    movq    [bx], mm0
```

## What it needs

- If-conversion at the **source shape** level — the same recognition
  [O0108](O0108-branchless-select.md) needs scalar-side, reused here.
- Both arms must be side-effect-free and trap-free, since every lane is computed.
- Signed/unsigned compare selection per element type, and the mask-extraction
  path (`PMOVMSKB`) for the counting shapes.
