# O0013 — Promotion lowering

| | |
|---|---|
| **Status** | ✅ Implemented (16-bit and 32-bit forms) |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Expressions.cs` — `EmitModularInt16`, the 32-bit promotion path, `StoreFoldedPromoted` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF113.BAS`, scenario `LongArithmeticStaysOffTheFpu` |
| **Related** | [O0012](O0012-float-demotion.md), [O0016](O0016-value-fact-analysis.md), [O0055](O0055-ir-integer-recovery.md) |
| **Split into** | [O0212](O0212-promotion-lowering-32.md) |

## What it is

PowerBASIC 2.0+ computes integral `+`, `-` and `*` **in floating point** — that
is why `PRINT A% * B%` can show `9E+8` instead of a wrapped 16-bit product. The
faithful lowering therefore drives integer arithmetic through the x87: `FILD`
each operand, the FPU op, `FISTP` into a staging cell, then load the result.

**This page covers the 16-bit form**, which is unconditionally legal: a 1- or
2-byte store **wraps**, and the low bits of the exact x87 result *are* the
modular result at every depth of the tree. So `+ - *`/negate trees over 16-bit
integral leaves assigned into 16-bit integral targets run on the plain ALU.

The 32-bit form is conditional — a 4-byte store does not wrap — and is the
separate entry [O0212](O0212-promotion-lowering-32.md).

## Sample

```basic
DIM a%, b%, c%
c% = a% * 3 + b%

DIM total&, delta&
total& = total& + delta&
```

## Without the optimizer

```asm
    fild    word ptr [a]
    fild    word ptr [three]
    fmul
    fild    word ptr [b]
    fadd
    fistp   word ptr [temp]
    mov     ax, [temp]
    mov     [c], ax
    ; and for the LONG add:
    fild    dword ptr [total]
    fild    dword ptr [delta]
    fadd
    fistp   dword ptr [temp32]
    mov     ax, [temp32]
    mov     dx, [temp32+2]
    mov     [total], ax
    mov     [total+2], dx
```

## With the optimizer

```asm
    mov     ax, [a]
    mov     bx, 0003h
    imul    bx
    add     ax, [b]
    mov     [c], ax
    ; LONG add, with the range guard
    mov     ax, [total]
    mov     dx, [total+2]
    add     ax, [delta]
    adc     dx, [delta+2]
    jno     Ok
    mov     ax, 0000h        ; reproduce the x87 out-of-range sentinel
    mov     dx, 8000h
Ok:
    mov     [total], ax
    mov     [total+2], dx
```

## Equivalent BASIC

The observable program is unchanged; what disappears is the round trip:

```basic
c% = a% * 3 + b%          ' computed on the 16-bit ALU, wrapping as PB's store does
total& = total& + delta&  ' computed on the 32-bit ALU, sentinel on overflow
```

## Why it is safe

- **16-bit**: modular arithmetic commutes with intermediate wrapping —
  `(a*2 + b*3) mod 2¹⁶ = ((a*2 mod 2¹⁶) + (b*3 mod 2¹⁶)) mod 2¹⁶` — so the tree
  may be evaluated integrally at every depth.
- The tree walk explicitly checks the x87's 64-bit **mantissa budget**, which
  closed a latent hole where a deep enough product could exceed it.
- **32-bit**: the pass only fires when the value provably fits, or when the
  guard reproduces the exact sentinel the float store would have left.
- Checked arithmetic stays integral in the binder and never reaches this
  lowering.
- Constant folding had to learn the same store semantics: `StoreFoldedPromoted`
  reproduces the sentinel rather than wrapping, because an optimizer that
  changes a program's output is not an optimization.
