# O0189 — Multiply by `2^a ± 2^b`

| | |
|---|---|
| **Status** | ✅ Implemented (16-bit integer multiply) |
| **Stage** | IR middle end + x86 back end (instruction selection) |
| **Source** | `Ir/Passes/VerifiedArithmeticLowering.cs` — `LowerMultiply` (two-term forms); `Backend/InstructionSelector.cs` — `TryDecomposeConstantMultiply` (three and four set bits) |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` |
| **Verified by** | `tests/diff/DIFF42.BAS` |
| **Split from** | [O0004](O0004-strength-reduction.md) (which is now the power-of-two multiply only) |

## What it is

Multipliers beyond a single power of two:

| Multiplier shape | Lowered to |
|---|---|
| `2^a + 2^b` (two bits set) | `v<<a + v<<b` |
| `2^a - 2^b` (a contiguous run of set bits) | `v<<a - v<<b` |
| a negative multiplier | a two-term form with the operands of the subtraction swapped (`-6` = `v<<1 - v<<3`), or a trailing negate |
| three set bits (four where the target's cost model prefers it) | a shift-add chain threading `v<<k` through one temporary |
| anything else | left as the compact `IMUL` |

The two-term forms are chosen in the IR middle end, and each candidate is
checked against the real product over all 65536 inputs before it is used. The
three- and four-bit chains are built by the instruction selector for whatever
multiply is left.

## Sample

```basic
$OPTIMIZE SPEED
DIM v%, r%
r% = v% * 10                 ' 10 = 2^3 + 2^1
```

## With the optimizer

```asm
    mov     ax, [v]
    mov     bx, ax
    shl     ax, 1            ; v<<(3-1) staged
    shl     ax, 1
    add     ax, bx
    shl     ax, 1            ; << b
```

The current lowering computes the two terms separately — `v<<3` and `v<<1`,
each as repeated `SHL r,1` — and adds them, rather than factoring out the common
`<< b` as shown above.

## Why it is safe

The IR's 16-bit `mul` is modular, so every chain only has to reproduce the
product's low 16 bits, which the exhaustive check confirms. Under `$ERROR
OVERFLOW ON` the multiply is lowered one width up and range-checked before being
truncated, so there is no 16-bit multiply for either rewrite to match
([O0004](O0004-strength-reduction.md)). Both rewrites apply only under
`$OPTIMIZE SPEED`, since the chain is larger than the `IMUL`.

## Limits

The general cost-model-driven decomposition for every constant and every path is
[O0078](O0078-multiply-decomposition.md).
