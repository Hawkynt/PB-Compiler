# O0237 — `MOVZX`/`MOVSX` byte loads

| | |
|---|---|
| **Status** | ⬜ Not implemented on the IR path |
| **Stage** | x86 back end (instruction selection, planned) |
| **Source** | None. `Backend/InstructionSelector.cs` widens a byte with `XOR AH,AH` / `CBW` on every CPU; `MOVZX`/`MOVSX` are not in the back end's `MOpcode` set (`Backend/MachineIr.cs`) |
| **Gate** | `--optimize` + `$CPU 80386` |
| **Split from** | [C0001](C0001-386-codegen.md) |

## What it is

A `BYTE`/`SBYTE` cell read widens in **one** instruction instead of a load plus
a separate extension.

Not implemented on the IR path; the syntax-level version was retired with the
direct emitter. The back end stages a byte through AL and extends it with
`XOR AH,AH` or `CBW` regardless of `$CPU`.

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
