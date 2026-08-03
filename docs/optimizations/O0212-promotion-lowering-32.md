# O0212 — 32-bit promotion lowering

| | |
|---|---|
| **Status** | ✅ Implemented (2026-08) |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Expressions.cs` (32-bit promotion path), `StoreFoldedPromoted` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF113.BAS`, scenario `LongArithmeticStaysOffTheFpu` |
| **Split from** | [O0013](O0013-promotion-lowering.md) (which is now the 16-bit form) |

## What it is

`total& = total& + delta&` lowered faithfully is `FILD` / the x87 op / `FISTP`
plus a memory staging cell at each end — eleven instructions and two round trips
for what the integer ALU does in two.

The 16-bit form is unconditionally legal because a 1- or 2-byte store **wraps**.
A 4-byte store does **not**: an out-of-range value comes back as the x87's
integer-indefinite pattern `8000_0000h`. So the 32-bit form fires when the value
provably cannot leave the destination's range — plus one rescued shape, a single
`+`/`-` over exactly-representable operands, guarded by three instructions,
because the ALU's overflow flag says precisely when the true 33-bit result left
the range.

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

## Why it is safe

The tree walk checks the x87's **64-bit mantissa budget** explicitly — which
also closed a latent hole where a deep enough product could exceed it — and the
guard reproduces the sentinel bit for bit. `StoreFoldedPromoted` teaches the same
store semantics to constant folding, so `--optimize` cannot print `-2` where the
faithful build prints `-2147483648`.
