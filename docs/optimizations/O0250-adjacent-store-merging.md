# O0250 — Adjacent store merging

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0136](O0136-adjacent-access-merging.md), [O0033](O0033-constant-store.md), [C0001](C0001-386-codegen.md) |
| **Split from** | [O0136](O0136-adjacent-access-merging.md) |

## The idea

Consecutive stores to adjacent cells combine into one wider store. With constant
values the merged immediate is folded at compile time; with computed values the
parts are assembled in a register first.

## Applies to

```basic
TYPE Rgb
  r AS BYTE
  g AS BYTE
  b AS BYTE
  a AS BYTE
END TYPE
DIM p AS Rgb
p.r = 1 : p.g = 2 : p.b = 3 : p.a = 4
```

```asm
    mov     dword ptr [p], 04030201h    ; one store instead of four
```

## What it needs

- Adjacency, alignment and **little-endian** ordering of the merged constant.
- No barrier in between that could observe the partial state — and hardware
  registers reached through `DIM … AT` or `POKE` must be excluded outright,
  since a device may care about the individual writes.
- Under `$ERROR` modes, the individual stores' trap behavior must be preserved.
