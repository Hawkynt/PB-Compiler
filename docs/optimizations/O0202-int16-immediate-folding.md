# O0202 — 16-bit immediate operand folding

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | x86 back end (instruction selection) |
| **Source** | `Backend/InstructionSelector.cs` — `SelectBinary`, `TryOperand`, `EmitCompareForFlags`; `Ir/Passes/InstCombine.cs` — `SimplifyCmp` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF43.BAS` (add/sub), `DIFF44.BAS` (bitwise/compare), `DIFF45.BAS` (checked add/sub) |
| **Split from** | [O0008](O0008-peephole-zero-idiom.md) (which is now the zero idiom only) |

## What it is

A compile-time-constant operand becomes an **immediate** instead of being
materialized in a register: `ADD/SUB/AND/OR/XOR AX,imm` and `CMP AX,imm`. That
drops the constant load *and* the `PUSH`/`POP` pair that staged the second
operand.

The selector maps an IR integer constant straight to an immediate operand. A
comparison with the constant on the left is swapped by `InstCombine` (predicate
mirrored), and the selector mirrors any that still reach it the same way.

## Sample

```basic
DIM v%, r%
r% = v% + 5
IF v% = 7 THEN PRINT "seven"
```

## Without / with

```asm
    mov     ax, [v]          ; without
    push    ax
    mov     ax, 0005h
    mov     bx, ax
    pop     ax
    add     ax, bx

    mov     ax, [v]          ; with
    add     ax, 0005h
```

## Why it is safe

The immediate is taken modulo 2¹⁶ — the same low word the register path would
have coerced into BX — so the result is bit-identical. Under `$ERROR OVERFLOW`
the trap is an explicit sign test the IR lowering adds around the operation, so
it does not depend on the form of the `ADD`.
