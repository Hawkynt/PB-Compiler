# O0033 — Constant store as immediate

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | x86 back end (instruction selection) |
| **Source** | `Backend/InstructionSelector.cs` — `SelectStore`, `TryOperand` (a constant operand is an immediate); the constant itself from `Ir/IrConstFold.cs` via SCCP/instcombine |
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

`SelectStore` asks `TryOperand` for the stored value, and an IR integer constant
comes back as an immediate, so the store is a single `MOV [cell], imm`. The
promoted conversion is gone before selection: the middle end folds it to that
constant.

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

The immediate is the IR constant the value folded to, so the question is what
`IrConstFold` folds: integer results wrap to the result type's width, and an
operation whose result would be undefined — among them a float-to-integer
conversion outside the 64-bit range — is not folded and keeps its runtime path.

Under `$ERROR NUMERIC/OVERFLOW` the range test is an explicit compare and a
branch to the raise in the IR. It folds away only when the constant is proven
in range; otherwise the raise stays, so the runtime check still fires where the
program is observed to raise it.
