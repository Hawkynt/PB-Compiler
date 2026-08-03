# O0256 — Vector select / blend

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0150](O0150-vector-compare-select.md), [O0051](O0051-ir-if-conversion.md), [R0004](R0004-asm-intrinsics.md) |
| **Split from** | [O0150](O0150-vector-compare-select.md) |

## The idea

Per-lane conditional assignment — the vector form of `x = IF(c, a, b)` — lowers
to `PAND`/`PANDN`/`POR` over a compare-generated mask on MMX/SSE2, or to a native
blend instruction where one exists.

## Applies to

```basic
$CPU 80586 MMX
FOR i% = 0 TO 999
  IF a%(i%) > 0 THEN c%(i%) = a%(i%) ELSE c%(i%) = 0
NEXT
```

```asm
    movq    mm2, mm0
    pcmpgtw mm2, mm3         ; lane mask
    pand    mm0, mm2         ; keep where true, zero elsewhere
```

## What it needs

- The mask comes from [O0150](O0150-vector-compare-select.md); this entry is the
  **selection** step that consumes it.
- Both arms are evaluated for every lane, so they must be side-effect-free and
  trap-free — the same requirement as the scalar if-conversion.
