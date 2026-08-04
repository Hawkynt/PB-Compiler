# O0078 — General multiply decomposition

| | |
|---|---|
| **Status** | 🟡 Partial — one-, two-, three- and four-set-bit multipliers (and contiguous runs) decompose on the modular int16 path; the four-bit chain is gated by the [O0174](O0174-target-cost-models.md) cost model (it fires only where the multiply is slow — the 8086 tier — and keeps the compact IMUL on a 386+); five-plus-bit remains |
| **Stage** | Emitter |
| **Related** | [O0004](O0004-strength-reduction.md), [O0064](O0064-lea-fusion.md), [O0174](O0174-target-cost-models.md) |

## The idea

[O0004](O0004-strength-reduction.md) reduces powers of two everywhere, and
under `$OPTIMIZE SPEED` handles `2^a ± 2^b` multipliers on the modular int16
path. Generalize that to **any** small constant, on every integer path, chosen
by a cost model rather than by pattern count:

```
x * 10  ->  (x << 3) + (x << 1)
x * 3   ->  (x << 1) + x            (or LEA, see O0064)
x * 100 ->  ((x << 2) + x) << 2 + ((x << 2) + x) << ...   ; decomposition search
```

## Applies to

```basic
DIM x%, y&
y& = x% * 10
```

## Today

An 8086 `IMUL` (~120–140 cycles for a 16×16 product) or, for a promoted tree,
the full x87 round trip.

## Now — up to three set bits decompose

Under `$OPTIMIZE SPEED`, `TryEmitModularConstMul` handles a multiplier with **one,
two or three set bits**, plus every contiguous run (`2^a - 2^b`). The three-bit
case `m = 2^a + 2^b + 2^c` factors out `2^c` and threads the two shifted terms
through one register — no memory temp:

```asm
    ; y% = x% * 11   (11 = 8 + 2 + 1)
    mov     ax, [x]
    mov     bx, ax
    shl     bx, 1            ; x<<1
    add     ax, bx           ; x + x<<1
    shl     bx, 2            ; x<<3   (bx was x<<1, now x<<3)
    add     ax, bx           ; x*11
```

`11`, `13`, `25`, `44`, … now emit shifts and adds instead of `IMUL`, verified
byte-identical against the genuine oracle across the sign range and the modular
wrap (`30000 * 11` → `2320`); a regression test confirms `x% * 11` decomposes.

**Four set bits** (`23`, `85`, `105`, …) generalise the same BX-threaded chain —
one (shift, add) per extra term — but at ~8 instructions they are no longer a
win on every target, so they are the first decomposition gated by the
[O0174](O0174-target-cost-models.md) **cost model**: `x% * 23` decomposes at the
default 8086 tier (where the ~124-cycle `MUL` dwarfs the chain) and keeps the
compact `IMUL` under `$CPU 80386` (where the multiply is ten-ish cycles and the
chain would lose). A regression test pins both directions, and the optimization
battery runs `n% * 85` under DOSBox with the optimizer on and off for the
runtime cross-check.

## Still planned — five-plus bits

- **Five-or-more set bits** still keep the compact `IMUL`; the chain length now
  outweighs even the 8086 multiply, and the cost model would decline them anyway.
- The two- and three-bit chains ship unconditionally under SPEED — a clear win on
  every `$CPU`-reachable target — while the four-bit chain asks the cost model;
  wiring the two/three-bit forms through the same query is a mechanical follow-up.
- Soundness is [O0004](O0004-strength-reduction.md)'s: only on the unchecked,
  modular path, where every chain reproduces the product's low bits exactly.
  Under `$ERROR OVERFLOW` the real `IMUL` and its `JNO` guard survive.

Native-only. The IR back ends leave a `* constant` for LLVM / the host C
compiler, which run their own multiplier decomposition against the real target's
cost model.
