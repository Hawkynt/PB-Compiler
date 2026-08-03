# O0193 — Subscript scaling by shift

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Arrays.cs` |
| **Gate** | `--optimize` |
| **Verified by** | scenario `AccumulateOverArrayIsHandQuality` |
| **Split from** | [O0004](O0004-strength-reduction.md) |

## What it is

A subscript is scaled by the element size before it is added to the base. For a
power-of-two element size that scaling is a shift — and using a multiply there
is not merely slower but **wrong for the declared target**: `IMUL r,r,imm` is an
80186 instruction, which an 8086 does not have.

## Sample

```basic
DIM a%(0 TO 99), i%, v%
v% = a%(i%)
```

## Without / with

```asm
    mov     ax, [i]          ; without
    imul    ax, ax, 2        ; ~21 cycles, and an 80186 encoding
    mov     bx, ax

    mov     ax, [i]          ; with
    shl     ax, 1            ; 2 cycles, 8086-legal
    mov     bx, ax
```

## Why it is safe

The scaled index is the same 16-bit value either way — the multiply and the
shift agree on the low word, and array addressing is unchecked 16-bit
arithmetic that wraps identically in both forms.

## See also

- Constant subscripts skip the computation entirely —
  [O0036](O0036-constant-subscript-folding.md).
- A loop walking the array does not need the scale at all —
  [O0030](O0030-induction-variable-strength-reduction.md).
