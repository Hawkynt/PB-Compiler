# O0136 — Adjacent load and store merging

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter / assembler |
| **Related** | [O0015](O0015-udt-zero-cost.md), [C0001](C0001-386-codegen.md), [O0137](O0137-load-widening.md) |
| **Split into** | [O0250](O0250-adjacent-store-merging.md) |

## The idea

Four adjacent byte loads are one 32-bit load; two adjacent word stores are one
dword store. The 8086 already benefits from byte → word merging (one bus cycle
instead of two for an aligned word), and `$CPU 80386` extends it to dwords.

The block-move paths already do this for whole `TYPE`s and strings
([O0015](O0015-udt-zero-cost.md), [R0003](R0003-string-engine.md)); what is
missing is the **scalar** case, where the adjacency is between independent
statements rather than inside one runtime routine.

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

## Today

```asm
    mov     byte ptr [p], 01h
    mov     byte ptr [p+1], 02h
    mov     byte ptr [p+2], 03h
    mov     byte ptr [p+3], 04h
```

## Planned

```asm
    mov     word ptr [p], 0201h      ; 8086
    mov     word ptr [p+2], 0403h
    ; or, under $CPU 80386:
    mov     dword ptr [p], 04030201h
```

## What it needs

- **Adjacency and alignment** proofs: the offsets must be contiguous and the
  base suitably aligned (a misaligned word access is legal on x86 but costs an
  extra bus cycle — a cost-model question).
- **Endianness** — the merged constant is little-endian, which is what makes the
  literal above `0201h` and not `0102h`.
- No intervening barrier that could observe the partial state (a call, inline
  asm, an interrupt-visible cell). Volatile-ish storage — hardware registers
  reached through `DIM … AT` or `PEEK`/`POKE` — must be excluded entirely.
