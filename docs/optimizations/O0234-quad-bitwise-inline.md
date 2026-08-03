# O0234 — Inline 64-bit bitwise operations

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter |
| **Source** | `CodeGen` — `EmitQuad386Bitwise` |
| **Gate** | `--optimize` + `$CPU 80386` |
| **Verified by** | `tests/diff/DIFF72.BAS` |
| **Split from** | [C0001](C0001-386-codegen.md) |

## What it is

`QUAD`/`QWORD` `AND`, `OR`, `XOR`, `EQV` and `IMP` run inline as two 32-bit
halves in EAX, instead of calling the `QuadAnd`-family runtime routines.

## Sample

```basic
$CPU 80386
DIM a AS QUAD, b AS QUAD, c AS QUAD
c = a AND b
```

## Without / with

```asm
    ; without: push both operands, call the runtime routine, collect the result
    call    rt_quad_and

    mov     eax, [a]         ; with
    and     eax, [b]
    mov     [c], eax
    mov     eax, [a+4]
    and     eax, [b+4]
    mov     [c+4], eax
```

## Why it is safe

Bitwise operations **cannot trap** — no overflow, no division, no conversion —
so the inline form needs no guard and is unconditionally equivalent. That is
precisely why the bitwise operators were the first QUAD family to come off the
runtime, while QUAD add/subtract/multiply deliberately stay on the x87 to match
PBC's lossy behavior beyond 2⁵³.
