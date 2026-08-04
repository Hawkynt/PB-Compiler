# O0078 — General multiply decomposition

| | |
|---|---|
| **Status** | 🟡 Partial — one-, two- and three-set-bit multipliers (and contiguous runs) decompose on the modular int16 path; four-plus-bit and a per-target cost model remain |
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
wrap (`30000 * 11` → `2320`); a regression test confirms `x% * 11` decomposes
while `x% * 23` (four bits) keeps `IMUL`.

## Still planned — four-plus bits and the cost model

- **Four-or-more set bits** (`23`, `105`, …) still keep the compact `IMUL`; a
  general decomposition search would cover them.
- A **cost model** ([O0174](O0174-target-cost-models.md)) is what makes the
  general case target-dependent: a shift/add chain is substantially faster than
  `IMUL` on an 8086 while being larger, and on a Pentium the `IMUL` wins back, so
  the decomposition depth must be chosen per target. The three-bit chain is a
  clear 8086 win at any depth, which is why it ships unconditionally under SPEED.
- Soundness is [O0004](O0004-strength-reduction.md)'s: only on the unchecked,
  modular path, where every chain reproduces the product's low bits exactly.
  Under `$ERROR OVERFLOW` the real `IMUL` and its `JNO` guard survive.

Native-only. The IR back ends leave a `* constant` for LLVM / the host C
compiler, which run their own multiplier decomposition against the real target's
cost model.
