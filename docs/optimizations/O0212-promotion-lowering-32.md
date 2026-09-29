# O0212 — 32-bit promotion lowering

| | |
|---|---|
| **Status** | ✅ Implemented (2026-08) |
| **Stage** | IR middle end (runs in the legalization pipeline too) |
| **Source** | `Ir/Passes/IntegerRecovery.cs` — `TryRecover`, `FitsSigned` (ranges from `Ir/Analysis/IrRangeAnalysis.cs`); scheduled by `Ir/Passes/IrMiddleEndPipeline.cs` in both `Legalize` and `Standard` |
| **Gate** | none — the native build always enables `recoverIntegerArithmetic` |
| **Verified by** | `tests/diff/DIFF113.BAS`, scenario `LongArithmeticStaysOffTheFpu` |
| **Split from** | [O0013](O0013-promotion-lowering.md) (which is now the 16-bit form) |

## What it is

`total& = total& + delta&` lowered faithfully is `FILD` / the x87 op / `FISTP`
plus a memory staging cell at each end — eleven instructions and two round trips
for what the integer ALU does in two.

`IntegerRecovery` rewrites a float tree that is converted back into an integer
(`fptosi`) into the same tree of integer `add`/`sub`/`mul` when every leaf is an
integer of the destination's width, a narrower integer (widened first, so
`L& = A% * B%` stays exact), a wider integer that `IrRangeAnalysis` proves fits
the destination, or a float constant that is an exact integer. The result wraps
modulo 2³², which is what genuine PBC 3.50 stores for a 4-byte `+`/`-`
(`DIFF113.BAS`: `2147483000 + 1000` stores `-2147483296`, not the x87's
`8000_0000h`).

## Sample

```basic
DIM total&, delta&
total& = total& + delta&
```

## With the optimizer

```asm
    mov     ax, [total]
    mov     dx, [total+2]
    add     ax, [delta]
    adc     dx, [delta+2]
    jno     Ok
    mov     ax, 0000h        ; reproduce the x87 sentinel exactly
    mov     dx, 8000h
Ok:
    mov     [total], ax
    mov     [total+2], dx
```

The `JNO`/sentinel guard above is the retired direct emitter's output; the IR
path emits the plain `ADD`/`ADC` pair and stores the wrapped result.

## Why it is safe

Modular arithmetic commutes with the intermediate wrapping, so an integer tree
over the same leaves stores the same low 32 bits as the promoted computation
would after wrapping. Only `+`, `-`, `*` and precision casts are walked; any other
float operation, or a wider leaf without a range proof, leaves the tree on the
FPU. Under `$ERROR OVERFLOW` the binder keeps 4-byte `+`/`-` integral, and the
lowering adds its explicit overflow test instead. Functions with an armed error
handler are not rewritten.
