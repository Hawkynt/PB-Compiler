# O0087 — Rematerialization

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Register allocation |
| **Related** | [O0058](O0058-386-register-allocation.md), [O0086](O0086-spill-slot-reuse.md), [O0001](O0001-constant-folding.md) |

## The idea

When an allocator runs out of registers it spills — a store now and a reload
later. But some values are cheaper to *recompute* than to store and reload: a
constant (`0`, `1`, a literal), an address (`LEA` of a fixed cell), or a trivial
expression over values that are still live (`i + 1`).

On an 8086, `XOR AX,AX` is 2 bytes and 3 cycles against a store plus a reload at
two memory accesses — so rematerialization is not a micro-optimization, it is
usually the correct choice for anything constant.

## Applies to

```basic
DIM i%, j%, k%, base%
' a register-pressured region where the constant 1 and base%+1 are both live
```

## Today

Values are spilled and reloaded uniformly; there is no notion of a cheaply
recomputable value.

## Planned

```asm
    ; instead of  mov [bp-8],ax  ...  mov ax,[bp-8]
    xor     ax, ax           ; rematerialized where needed
```

## What it needs

- A **rematerializability predicate**: the value must be recomputable from
  operands that are still live at the reload point, with no side effects and no
  trap possibility.
- A cost comparison against the spill ([O0174](O0174-target-cost-models.md)),
  since on some targets the store/reload pair is cheaper than a long
  recomputation.
- It only becomes relevant once there is an allocator that spills at all —
  [O0058](O0058-386-register-allocation.md).
