# O0191 — Modulo by a power of two

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR middle end |
| **Source** | `Ir/Passes/VerifiedArithmeticLowering.cs` — `LowerSignedDivision`, `LowerSignedPowerOfTwo` (INTEGER); `Ir/Passes/InstCombine.cs` (unsigned `x MOD 2^k` → `AND`) |
| **Gate** | `--optimize` (legal under every `$ERROR` mode) |
| **Verified by** | `tests/diff/DIFF27.BAS` |
| **Split from** | [O0004](O0004-strength-reduction.md) |

## What it is

`x MOD 2^n` becomes a mask — but PB's remainder takes the **dividend's** sign,
so the signed 16-bit form reconstructs it as `((x + b) AND mask) - b` where `b`
is the sign bias, checked against the real remainder over all 65536 dividends
before it is used. An unsigned modulo is a plain `AND`.

## Sample

```basic
DIM n%, r%
r% = n% MOD 8
```

## Without / with

```asm
    mov     ax, [n]          ; without: a full IDIV for the remainder
    mov     bx, 0008h
    or      bx, bx
    jz      rt_err_div
    cwd
    idiv    bx
    mov     ax, dx

    mov     ax, [n]          ; with: mask plus the sign fix-up
    cwd
    and     dx, 0007h
    add     ax, dx
    and     ax, 0007h
    sub     ax, dx
```

The current selection builds the bias with `ADD r,r / SBB r,r / AND r,0007h` in
a scratch register rather than with `CWD` into `DX`; the rest is as shown.

## Why it is safe

A positive constant divisor can neither raise Error 11 nor overflow, and the
bias/un-bias pair reproduces the dividend-signed remainder exactly for every
input, including `MININT`.

## See also

When the modulo is only **compared to zero**, even the reconstruction is dead —
[O0192](O0192-parity-mask.md).
