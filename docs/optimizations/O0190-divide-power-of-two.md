# O0190 — Integer divide by a power of two

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR middle end + x86 back end (instruction selection) |
| **Source** | `Ir/Passes/VerifiedArithmeticLowering.cs` — `LowerSignedDivision`, `LowerSignedPowerOfTwo` (INTEGER); `Ir/Passes/InstCombine.cs` (unsigned `x / 2^k` → shift); `Backend/InstructionSelector.cs` — `SelectConstantShift` |
| **Gate** | `--optimize` (legal under every `$ERROR` mode) |
| **Verified by** | `tests/diff/DIFF27.BAS` |
| **Split from** | [O0004](O0004-strength-reduction.md) |

## What it is

`x \ 2^n` becomes an arithmetic shift — with PB's **truncation fix-up**, because
`SAR` rounds toward negative infinity while `\` truncates toward zero. The
signed 16-bit form biases a negative dividend by `2^n - 1` before shifting (a
negative power-of-two divisor adds a final negate), and the formula is checked
against the real quotient over all 65536 dividends before it is used. An
unsigned divide is a plain logical shift (`SHR`).

Shift counts stay 8086-safe: up to four one-bit shifts inline, `CL` beyond that;
the shift-by-immediate form is used only when the target CPU is an 80186 or
later. The `SAR 15` that produces the sign mask is selected as `ADD r,r / SBB r,r`.

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

The current selection builds the bias with `ADD r,r / SBB r,r / AND r,0003h` in
a scratch register rather than with `CWD` into `DX`; the rest is as shown.

## Why it is safe

A positive constant divisor can raise neither Error 11 nor a quotient overflow,
so the lowering is legal under every `$ERROR` mode — unlike the multiply
reduction, which must back off under `$ERROR OVERFLOW`.
