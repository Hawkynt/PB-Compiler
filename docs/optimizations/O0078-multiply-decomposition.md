# O0078 — General multiply decomposition

| | |
|---|---|
| **Status** | ⬜ Planned (the modular int16 path already has a two-term/contiguous-run form — [O0004](O0004-strength-reduction.md)) |
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

## Planned

```asm
    mov     ax, [x]
    mov     bx, ax
    shl     ax, 1            ; x*2
    mov     cx, ax
    shl     ax, 1
    shl     ax, 1            ; x*8
    add     ax, cx           ; x*10
```

## What it needs

- A **cost model** ([O0174](O0174-target-cost-models.md)). This is exactly the
  case where instruction count lies: a three-instruction shift/add chain is
  substantially faster than `IMUL` on an 8086 while being larger, and on a
  Pentium the `IMUL` wins back. The decomposition must be chosen per target.
- The soundness argument [O0004](O0004-strength-reduction.md) already
  establishes: only on unchecked, modular paths, where every chain reproduces the
  product's low bits exactly. Under `$ERROR OVERFLOW` the real `IMUL` and its
  `JNO` guard must survive.
