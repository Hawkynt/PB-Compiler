# O0004 — Strength reduction

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Optimize.cs` — `#region O4 - multiply strength reduction` |
| **Gate** | `--optimize`; backs off under `$ERROR OVERFLOW ON` |
| **Verified by** | `tests/diff/DIFF41.BAS` |
| **Related** | [O0030](O0030-induction-variable-strength-reduction.md), [O0036](O0036-constant-subscript-folding.md), [O0056](O0056-reciprocal-division.md), [O0064](O0064-lea-fusion.md) |
| **Split into** | [O0189](O0189-multiply-shift-add-shapes.md), [O0190](O0190-divide-power-of-two.md), [O0191](O0191-modulo-power-of-two.md), [O0192](O0192-parity-mask.md), [O0193](O0193-subscript-shift-scaling.md) |

## What it is

**This page covers the power-of-two multiply**; the sibling lowerings each have
their own entry (see *Split into* above).

`x * 2^n` becomes a shift chain, and `* 0` / `* 1` fold away entirely. Operand
side effects are preserved, and the shift counts stay 8086-safe: up to four
one-bit shifts inline, `CL` beyond that — never the 186+ shift-by-immediate
form.

## Sample

```basic
DIM n%, a%
a% = n% * 8
```

## Without the optimizer

```asm
    mov     ax, [n]
    mov     bx, 0008h
    imul    bx               ; ~120 cycles on an 8086
    mov     [a], ax
```

## With the optimizer

```asm
    mov     ax, [n]
    mov     cl, 3
    shl     ax, cl           ; ~6 cycles
    mov     [a], ax
```

## Equivalent BASIC

```basic
a% = n% << 3                 ' the pb36 spelling of the same thing
```

## Why it is safe

- A left shift by `n` and a multiply by `2^n` agree on the low 16 bits, which is
  what the modular int16 path stores — and that path only ever runs unchecked.
- Under `$ERROR OVERFLOW ON` the reduction **backs off**: a shift chain cannot
  raise the genuine `IMUL`'s Error 6, so the checked multiply keeps its real
  instruction and its `JNO` guard (oracle battery `tests/diff/DIFF41.BAS`).
- `* 0` and `* 1` still evaluate the operand when it could have an effect.
