# O0206 — In-place memory `INCR`/`DECR`

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.cs` (the `INCR`/`DECR` statement path) |
| **Gate** | `--optimize` |
| **Split from** | [O0008](O0008-peephole-zero-idiom.md) |

## What it is

`INCR n%` on a non-resident 2-byte integer whose address costs no code updates
the **cell** directly instead of loading it, incrementing the accumulator and
storing it back.

## Sample

```basic
DIM n%
INCR n%
```

## Without / with

```asm
    mov     ax, [n]          ; without: three instructions, two memory accesses
    inc     ax
    mov     [n], ax

    inc     word ptr [n]     ; with: one instruction, one read-modify-write
```

## Why it is safe

The read-modify-write form computes the same value and sets the same flags
(including OF for the `$ERROR OVERFLOW` guard). It applies only to a direct cell
— a variable that is register-resident updates the register instead
([O0194](O0194-accumulator-residency.md)), and an indexed or indirect target
keeps the ordinary path.
