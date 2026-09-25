# O0004 — Strength reduction

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR middle end (rewrite) + x86 back end (instruction selection) |
| **Source** | `Ir/Passes/InstCombine.cs` — `Pow2Shift`; `Backend/InstructionSelector.cs` — `SelectConstantShift`, `SelectVariableShift` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF41.BAS` |
| **Related** | [O0030](O0030-induction-variable-strength-reduction.md), [O0036](O0036-constant-subscript-folding.md), [O0056](O0056-reciprocal-division.md), [O0064](O0064-lea-fusion.md) |
| **Split into** | [O0189](O0189-multiply-shift-add-shapes.md), [O0190](O0190-divide-power-of-two.md), [O0191](O0191-modulo-power-of-two.md), [O0192](O0192-parity-mask.md), [O0193](O0193-subscript-shift-scaling.md) |

## What it is

**This page covers the power-of-two multiply**; the sibling lowerings each have
their own entry (see *Split into* above).

`InstCombine` rewrites `x * 2^n` as `x << n`, and `* 0` / `* 1` / `* -1` fold
to `0`, `x` and `0 - x`. Instruction selection then spells the shift for the
target: on an 8086, up to four one-bit shifts inline and `CL` beyond that; the
186+ shift-by-immediate form only when the target CPU has it.

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
    shl     ax, 1
    shl     ax, 1
    shl     ax, 1            ; ~6 cycles
    mov     [a], ax
```

## Equivalent BASIC

```basic
a% = n% << 3                 ' the pb36 spelling of the same thing
```

## Why it is safe

- A left shift by `n` and a multiply by `2^n` agree on every bit of the IR
  type's width, and the IR's integer multiply is modular.
- Under `$ERROR OVERFLOW ON` a signed multiply is lowered as a product one
  width up followed by an explicit range check that raises Error 6
  (`IrLowering.CheckedMultiply`); rewriting that wider multiply as a shift
  leaves the check intact (oracle battery `tests/diff/DIFF41.BAS`).
- `* 0` and `* 1` still evaluate the operand when it could have an effect: the
  operand is a separate SSA instruction, and `Dce` removes it only when it has
  no observable effect.
