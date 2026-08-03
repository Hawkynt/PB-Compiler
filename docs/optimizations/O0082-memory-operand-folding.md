# O0082 — Memory operand folded into arithmetic

| | |
|---|---|
| **Status** | ⬜ Planned as a general rule (specific loop shapes already do it — [O0005](O0005-register-residency.md), [O0030](O0030-induction-variable-strength-reduction.md)) |
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

## Planned

```asm
    mov     ax, [s]
    add     ax, [x]
    mov     [s], ax
```

## What it needs

- The **operand-position** decision has to move into the binary emitter: pick
  which side becomes the register and which stays a memory operand, which is a
  small cost model of its own (a direct cell is one displacement; an indexed
  element needs BX anyway).
- Trap ordering under `$ERROR BOUNDS`: folding an array read into the ALU op is
  only legal when the bounds check is emitted where the separate load would have
  run it.
- Interaction with [O0038](O0038-instruction-scheduling.md): a folded operand
  gives the scheduler less to reorder, so the two want the same cost model.
