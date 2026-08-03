# O0203 — 32-bit immediate operand folding

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Expressions.cs` (int32 binary path) |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF47.BAS` |
| **Split from** | [O0008](O0008-peephole-zero-idiom.md) |

## What it is

The LONG/DWORD path folds a constant the same way as the 16-bit one, into
immediate **pair** operations:

- `AND`/`OR`/`XOR` on the low word in AX and the high word in DX;
- `ADD AX,imm` + `ADC DX,imm` (and `SUB`/`SBB`), keeping the `JNO` trap;
- `=`/`<>` by subtracting the halves and testing for zero — and against `0` the
  operand's own `AX|DX` already decides, so the subtract is skipped entirely.

That last case is the everyday `ptr& = 0` null test.

## Sample

```basic
DIM p&, t&
t& = p& + 100
IF p& = 0 THEN PRINT "null"
```

## Without / with

```asm
    ; without: load the constant into CX:BX, push/pop the pair, then add
    mov     ax, [p]
    mov     dx, [p+2]
    add     ax, 0064h        ; with
    adc     dx, 0000h
    ...
    mov     ax, [p]          ; the zero test
    or      ax, [p+2]
    jnz     NotNull
```

## Why it is safe

Each half is combined with the corresponding half of the constant, and the carry
chain (`ADC`/`SBB`) reproduces the 32-bit arithmetic exactly. The overflow flag
after the high-half operation is the 32-bit OF the trap needs.
