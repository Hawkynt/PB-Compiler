# O0013 — Promotion lowering

| | |
|---|---|
| **Status** | ✅ Implemented (16-bit and 32-bit forms) |
| **Stage** | IR (native x86 back end's legalizing and optimizing pipelines) |
| **Source** | `Ir/Passes/IntegerRecovery.cs` — `Run`, `TryRecover`; requested by `CodeGen/CodeGenerator.Backend.cs` (`recoverIntegerArithmetic: true`) and scheduled in `IrMiddleEndPipeline.Legalize()` and `Standard()` |
| **Gate** | native x86 back end; runs with and without `--optimize` |
| **Verified by** | `tests/diff/DIFF113.BAS`, scenario `LongArithmeticStaysOffTheFpu` |
| **Related** | [O0012](O0012-float-demotion.md), [O0016](O0016-value-fact-analysis.md), [O0055](O0055-ir-integer-recovery.md) |
| **Split into** | [O0212](O0212-promotion-lowering-32.md) |

## What it is

PowerBASIC 2.0+ computes integral `+`, `-` and `*` **in floating point** — that
is why `PRINT A% * B%` can show `9E+8` instead of a wrapped 16-bit product. The
IR lowering therefore emits such a tree as float arithmetic over `sitofp`
leaves, closed by a conversion back to the integer target. `IntegerRecovery`
finds each `fptosi` into an integer type whose operand is a tree of
`fadd`/`fsub`/`fmul` over `sitofp` leaves and integer-valued float constants,
and rewrites it as the integer `add`/`sub`/`mul` tree over the same values.

**This page covers the 16-bit form**, which is unconditionally legal: a 1- or
2-byte store **wraps**, and the low bits of the exact x87 result *are* the
modular result at every depth of the tree. So `+ - *` trees over 16-bit integral leaves assigned into 16-bit integral
targets run on the plain ALU. A leaf narrower than the target is sign- or
zero-extended first; a wider leaf is accepted only where the range analysis
proves it fits the target (`n% + LEN(s$)`).

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
- Any other node in the tree — a division, a call, a non-integral constant, a
  leaf of another width the ranges cannot bound — declines, and the float form
  is kept.
- Widening a narrower leaf into the target is exact: `L& = A2% * B2%` multiplies
  in 32 bits, where an f32 product would lose the low bit of `32767 * 32767`.
- A function with an armed error handler is never rewritten.
