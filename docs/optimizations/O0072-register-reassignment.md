# O0072 — Register reassignment (breaking AX serialization)

| | |
|---|---|
| **Status** | ⬜ Planned (a distinct subsystem, not a scheduler tweak) |
| **Stage** | Assembler / codegen boundary |
| **Related** | [O0038](O0038-instruction-scheduling.md), [O0058](O0058-386-register-allocation.md) |

## The idea

The scalar code generator is accumulator-centric: nearly every value passes
through AX. The [instruction scheduler](O0038-instruction-scheduling.md) can
therefore reorder only what is already independent — and after AX-serialization
almost nothing is. Renaming some value lifetimes onto BX/CX/DX would let
independent statement chains interleave, which is where the remaining pipeline
win lives.

## Applies to

```basic
$OPTIMIZE SPEED
DIM a%, b%, x%, y%
x% = a% * 3
y% = b% * 5
```

## Today

Both chains contend for AX, so the scheduler cannot interleave them:

```asm
    mov     ax, [a]
    mov     bx, 0003h
    imul    bx
    mov     [x], ax
    mov     ax, [b]
    mov     bx, 0005h
    imul    bx
    mov     [y], ax
```

## Planned

The second chain is renamed onto a free register and the two interleave.

## Why it is a separate subsystem

Two concrete reasons, both structural:

1. **Free-register proof.** Renaming a value's register lifetime is sound only
   if the target is dead across that lifetime. At the byte level a scheduling
   window is bounded by barriers where, conservatively, *every* register is
   live-out — so without an external liveness signal the only provably-free
   targets are registers redefined later in the same window, which never holds
   in exactly the AX-serial code the rename is meant to fix. It needs
   **codegen-supplied liveness**: the codegen knows almost nothing but SP/BP and
   the loop-induction SI/DI is live between scalar statements, so BX/CX/DX are
   genuinely free.
2. **Length-changing re-encoding.** The scalar codegen emits the accumulator
   short forms (`MOV AX,imm` = `B8`, `ADD AX,imm` = `05`). Renaming AX → BX turns
   `ADD AX,imm` into the modrm form `81 /0`, one byte longer — so a rename is
   **not** length-preserving and forces the window, and every label and fixup
   after it, to be re-laid-out.

That makes this a re-encoding register allocator (codegen liveness + per-operand
register-field tracking + function relayout), built and verified on its own —
not a byte-permute tweak on top of the scheduler.
