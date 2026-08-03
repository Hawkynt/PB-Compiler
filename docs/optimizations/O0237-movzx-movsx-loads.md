# O0237 — `MOVZX`/`MOVSX` byte loads

| | |
|---|---|
| **Status** | ✅ Implemented (2026-07) |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Places.cs` |
| **Gate** | `--optimize` + `$CPU 80386` |
| **Split from** | [C0001](C0001-386-codegen.md) |

## What it is

A `BYTE`/`SBYTE` cell read widens in **one** instruction instead of a load plus
a separate extension.

## Sample

```basic
$CPU 80386
DIM b AS BYTE, s AS SBYTE, n%, m%
n% = b
m% = s
```

## Without / with

```asm
    mov     al, [b]          ; without
    xor     ah, ah
    mov     al, [s]
    cbw

    movzx   ax, byte ptr [b] ; with
    movsx   ax, byte ptr [s]
```

## Why it is safe

`MOVZX`/`MOVSX` produce exactly the zero- and sign-extended values the two-step
sequences produce; the only difference is that they do not disturb the high half
of the register before writing it, which nothing downstream reads.

## See also

An extension whose result is already guaranteed correct should not be emitted at
all — [O0089](O0089-extension-elimination.md).
