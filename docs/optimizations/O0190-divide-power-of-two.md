# O0190 — Integer divide by a power of two

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Optimize.cs` (divide lowering) |
| **Gate** | `--optimize` (legal under every `$ERROR` mode) |
| **Verified by** | `tests/diff/DIFF27.BAS` |
| **Split from** | [O0004](O0004-strength-reduction.md) |

## What it is

`x \ 2^n` becomes an arithmetic shift — with PB's **truncation fix-up**, because
`SAR` rounds toward negative infinity while `\` truncates toward zero. The
signed form biases by `2^n - 1` before shifting; the DWORD form is unsigned
(plain `SHR`).

Shift counts stay 8086-safe: up to four one-bit shifts inline, `CL` beyond that,
never the 186+ shift-by-immediate form.

## Sample

```basic
DIM n%, q%
q% = n% \ 4
```

## Without / with

```asm
    mov     ax, [n]          ; without
    mov     bx, 0004h
    or      bx, bx
    jz      rt_err_div
    cwd
    idiv    bx

    mov     ax, [n]          ; with
    cwd
    and     dx, 0003h        ; bias by 2^n-1 when negative
    add     ax, dx
    sar     ax, 1
    sar     ax, 1
```

## Why it is safe

A positive constant divisor can raise neither Error 11 nor a quotient overflow,
so the lowering is legal under every `$ERROR` mode — unlike the multiply
reduction, which must back off under `$ERROR OVERFLOW`.
