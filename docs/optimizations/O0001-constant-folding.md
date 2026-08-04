# O0001 — Constant folding

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter, over the bound AST |
| **IR** | ✅ `Sccp` + `InstCombine` + `IrConstFold` in `IrPassManager.Standard()`; verified by `PortedMidEndOptimizationsTests` |
| **Source** | `CodeGen/CodeGenerator.Optimize.cs` — `#region O1`, `TryEmitFolded`, `FoldsWithoutWrap` |
| **Gate** | `--optimize` (on by default for `pb36`) |
| **Related** | [O0017](O0017-sccp.md) (cross-block constants), [O0025](O0025-pure-function-folding.md), [O0033](O0033-constant-store.md), [O0043](O0043-ir-instcombine.md) |

## What it is

An expression whose value the compiler can compute — literals, `CONST` equates
and operators over them — is not computed at run time. The emitter asks the
constant folder for the value, wraps it to the expression's **bound type**, and
emits a single literal load in place of the whole tree.

The wrap matters: `WrapToType` reproduces exactly what the 16- or 32-bit ALU
would have left in the destination, so the folded literal is bit-equal to the
value the program would have computed.

## When it fires

- The expression is integral-typed (or a string concatenation of literals — see
  [O0009](O0009-string-temp-economy.md)) and every leaf is a literal or equate.
- Every **computed** node's value fits its own type (`FoldsWithoutWrap`). This
  guard exists because the dialects differ: PB 2.0+ computes `+ - *` in floating
  point and never wraps mid-tree, but QuickBASIC, Turbo Basic and anything under
  `$COMPAT` wrap in place, where `32767 + 18` really is `-32751`. If any node
  leaves its type, the fold is abandoned and the genuine arithmetic emitted.
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

The folder is pure — it evaluates only operators over literal values, never a
call, an array read or anything that could trap. The result is wrapped to the
node's own type, and the `FoldsWithoutWrap` check rejects any tree whose
intermediate would have wrapped, so the emitted literal is the value the
un-folded code would have produced on every dialect.

## Limits

- Float-typed trees are left alone here; a constant *stored* into an integral
  cell is handled by [O0033](O0033-constant-store.md), which reproduces the x87
  store semantics (including the integer-indefinite sentinel for an out-of-range
  4-byte store).
- Shift-right and rotate are not folded (their result depends on operand width
  and signedness, which the type-less folder does not know); shift-left is.
- Constants that only become known across statements or blocks are
  [O0017](O0017-sccp.md)'s job.
