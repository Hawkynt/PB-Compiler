# O0001 — Constant folding

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR middle end |
| **Source** | `Ir/IrConstFold.cs` — `TryFold`, `Wrap`; applied by `Ir/Passes/InstCombine.cs` and `Ir/Passes/Sccp.cs` in `IrMiddleEndPipeline.Standard()` |
| **Verified by** | `PortedMidEndOptimizationsTests` |
| **Gate** | `--optimize` (on by default for `pb36`) |
| **Related** | [O0017](O0017-sccp.md) (cross-block constants), [O0025](O0025-pure-function-folding.md), [O0033](O0033-constant-store.md), [O0043](O0043-ir-instcombine.md) |

## What it is

An expression whose value the compiler can compute — literals, `CONST` equates
and operators over them — is not computed at run time. `IrConstFold.TryFold`
evaluates any binary operation, comparison or cast whose operands are all
constants and returns the result as a constant; `InstCombine` replaces the
instruction with it, and `Sccp` does the same for values that only become
constant through the control flow.

The wrap matters: `Wrap` sign-extends the result from the instruction's own
bit width, reproducing exactly what the 16- or 32-bit ALU would have left in
the destination, so the folded constant is bit-equal to the value the program
would have computed.

## When it fires

- Every operand of the instruction is a constant. Literals and equates are
  constants from lowering on; a folded result feeds the next instruction, so a
  whole tree collapses bottom-up.
- The dialect question — whether `32767 + 18` wraps to `-32751` or is computed
  wider — is settled before folding: lowering emits each operation at the type
  the binder gave it (floating expressions at the x87's 80-bit width), and the
  folder evaluates exactly that instruction at exactly that width.
- Calls are never folded here — that is [O0025](O0025-pure-function-folding.md).

## Sample

```basic
DIM h%, q%
h% = &HF0 OR &H0F
q% = 1000 \ 8
PRINT h%; q%
```

## Without the optimizer

Every operand is materialized and combined at run time:

```asm
    mov     ax, 00F0h        ; left operand
    push    ax
    mov     ax, 000Fh        ; right operand
    mov     bx, ax
    pop     ax
    or      ax, bx
    mov     [h], ax
    mov     ax, 03E8h        ; 1000
    push    ax
    mov     ax, 0008h
    mov     bx, ax
    pop     ax
    or      bx, bx           ; divide-by-zero guard (Error 11)
    jz      rt_err_div
    cwd
    idiv    bx
    mov     [q], ax
```

## With the optimizer

```asm
    mov     ax, 00FFh
    mov     [h], ax
    mov     ax, 007Dh
    mov     [q], ax
```

With [O0033](O0033-constant-store.md) the accumulator disappears too
(`mov word ptr [h], 00FFh`).

## Equivalent BASIC

```basic
DIM h%, q%
h% = 255
q% = 125
PRINT h%; q%
```

## Why it is safe

The folder is pure — it evaluates only operators over constant values, never a
call, an array read or anything that could trap. Operations whose result would
trap or be undefined are declined: division or remainder by zero,
`INT_MIN / -1`, out-of-range shift counts and out-of-range float-to-integer
conversions stay in the code for the runtime to handle. Integer results are
wrapped to the instruction's own type, so the constant is the value the
un-folded code would have produced.

## Limits

- Float arithmetic is folded only when the result is exact in a 64-bit double
  (tested with two-sum and fused multiply-add); otherwise the 80-bit x87 result
  could differ in the last bit, so the operation is left to the target. A
  constant *stored* into an integral cell is covered by
  [O0033](O0033-constant-store.md).
- Constants that only become known across statements or blocks are
  [O0017](O0017-sccp.md)'s job.
