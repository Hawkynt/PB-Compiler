# O0033 — Constant store as immediate

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Places.cs`, `StoreFoldedPromoted` |
| **Gate** | `--optimize`; a conversion that could trap keeps the ordinary path under `$ERROR NUMERIC/OVERFLOW` |
| **Verified by** | scenario `ConstantStoredAsImmediate` |
| **Related** | [O0001](O0001-constant-folding.md), [O0013](O0013-promotion-lowering.md) |

## What it is

`x = <integral constant>` into a cell whose address costs no code writes the
immediate **directly into memory** instead of staging it through the
accumulator. On the 8086 that is one instruction instead of two, and it also
takes a whole class of assignments off the FPU: `m% = -32768` is a LONG literal
with a float-promoted negation, so the plain path drove it through
`FILD`/`FCHS`/`FISTP`.

## Sample

```basic
DIM n%, m%
n% = 7
m% = -32768
```

## Without the optimizer

```asm
    mov     ax, 0007h
    mov     [n], ax
    fild    dword ptr [lit32768]
    fchs
    fistp   word ptr [temp]
    mov     ax, [temp]
    mov     [m], ax
```

## With the optimizer

```asm
    mov     word ptr [n], 0007h
    mov     word ptr [m], 8000h
```

## Equivalent BASIC

Unchanged.

## Why it is safe

The stored bits are exactly the ones the load-convert-store path would have
left. `StoreFoldedPromoted` reproduces the store semantics per width:

- a 1- or 2-byte target **wraps**, so the immediate is the wrapped value;
- a 4-byte signed target does **not** wrap — a value it cannot hold comes back
  as the x87's integer-indefinite pattern `8000_0000h`, and that is what is
  stored.

Under `$ERROR NUMERIC/OVERFLOW` a conversion that could trap keeps the ordinary
path, so the runtime check still fires where the program is observed to raise
it.
