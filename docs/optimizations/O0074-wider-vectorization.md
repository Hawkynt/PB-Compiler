# O0074 — Wider auto-vectorization

| | |
|---|---|
| **Status** | ⬜ Planned (the recognizer and all four encodings exist; the pattern set is narrow) |
| **Stage** | Emitter |
| **Related** | [O0026](O0026-auto-vectorization.md), [R0004](R0004-asm-intrinsics.md), [C0001](C0001-386-codegen.md) |

## The idea

[O0026](O0026-auto-vectorization.md) vectorizes exactly one shape:
`c(i) = a(i) OP b(i)` over rank-1 static 2-byte arrays with a constant trip
count. The infrastructure — MMX/SSE2/AVX/AVX-512 encoders, the `$CPU` feature
gate, the scalar tail, the wrap-correctness argument — carries much more:

- **reductions** — `s = s + a(i)` becomes a packed accumulate plus a horizontal
  add at the end;
- **scalar operand** — `c(i) = a(i) * k` with `k` broadcast into every lane;
- **4-byte elements** (`LONG`/`DWORD`) via `PADDD`/`PSUBD`;
- **byte elements** via `PADDB`/`PSUBB` for graphics masks;
- **variable trip counts**, with the tail computed at run time instead of
  unrolled at compile time;
- **compare/select shapes** — `IF a(i) > b(i) THEN c(i) = …` via
  `PCMPGTW` + `PAND`/`PANDN`.

## Applies to

```basic
$CPU 80586 MMX
$OPTIMIZE SPEED
DIM a%(0 TO 999), i%, s%
FOR i% = 0 TO 999
  s% = s% + a%(i%)          ' a reduction: not vectorized today
NEXT
```

## Planned

```asm
    pxor    mm1, mm1              ; four partial sums
Top:
    movq    mm0, [si]
    paddw   mm1, mm0
    add     si, 8
    loop    Top
    ; horizontal add of the four lanes, then the scalar tail
```

## What it needs

- **Reduction legality**: the partial sums wrap per lane exactly as the scalar
  accumulator would only because addition is associative modulo 2¹⁶ — true for
  `+`, `XOR`, `AND`, `OR`, but *not* for a saturating or float operation.
- A run-time tail for variable trip counts (the current tail is fully unrolled,
  which needs a constant remainder).
- The same gates as today: off under `$ERROR` checking, off when SI/DI are
  register-resident, scalar below 8 trips.
