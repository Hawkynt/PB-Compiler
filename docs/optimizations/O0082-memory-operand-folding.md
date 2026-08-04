# O0082 — Memory operand folded into arithmetic

| | |
|---|---|
| **Status** | ✅ Done (as a general lowering rule; only the left-operand-position cost model remains) |
| **Stage** | Emitter |
| **Related** | [O0005](O0005-register-residency.md), [O0030](O0030-induction-variable-strength-reduction.md), [O0034](O0034-redundant-load-elimination.md) |

## The idea

x86 ALU instructions take a memory operand directly. A load whose only consumer
is the next arithmetic instruction does not need to happen:

```asm
    mov     ax, [x]
    add     di, ax
```
becomes
```asm
    add     di, [x]
```

The accumulate loop already emits `ADD DI,[BX]`
([O0030](O0030-induction-variable-strength-reduction.md)), but as a
shape-specific emission, not as a general lowering rule — so the same pattern
outside that shape still pays for the staging move.

## Applies to

```basic
DIM x%, t%, s%
s% = s% + x%
t% = t% AND x%
```

## Today

```asm
    mov     ax, [s]
    push    ax
    mov     ax, [x]
    mov     bx, ax
    pop     ax
    add     ax, bx
    mov     [s], ax
```

## Now

```asm
    mov     ax, [s]
    add     ax, [x]
    mov     [s], ax
```

This is a **general lowering rule** in the binary emitter, not a loop shape.
`TryInt16MemOperand` returns the direct cell of a same-width scalar variable, and
`EmitBinary` folds it straight into `ADD/SUB/AND/OR/XOR AX,[mem]` (with the
push/pop staging only as the fallback). It covers:

- comparisons → `CMP AX,[mem]`;
- array elements → `FuseArrayElementOperand` (`ADD AX,[BX+disp]`), with the
  bounds check emitted where the separate load would have run it (`$ERROR BOUNDS`
  trap order preserved);
- 32-bit (`TryInt32MemOperand`), floats (`TryFloatMemOperand`), `MUL`, and the
  compound-assign / increment paths.

`s% = s% + x%` → `add ax,[x]` and `t% = s% AND x%` → `and ax,[x]` were both
confirmed in the emitted image. Gated on `this.Optimize`.

## Remaining refinement

- The **left-operand** position: a commutative op whose *left* side is the memory
  cell (`x% + s%` with `s%` in AX) still stages the left through a register. Folding
  it needs the operand-position cost model the header notes — a micro-optimization
  on top of the shipped rule, which already handles the far commoner right-operand
  and self-accumulate (`s = s + x`) shapes.

Native-only. On the IR back ends this is the C/LLVM instruction selector's job,
which folds memory operands against the real target's addressing modes.
