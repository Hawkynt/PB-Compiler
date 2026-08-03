# O0085 — Register copy coalescing

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Register allocation |
| **Related** | [O0027](O0027-copy-propagation.md), [O0058](O0058-386-register-allocation.md), [O0072](O0072-register-reassignment.md) |

## The idea

`MOV BX,AX` exists only because the producer and the consumer were assigned
different registers. When their live ranges do not interfere, they can share one
register and the move disappears — the standard coalescing step of a graph
colouring or linear-scan allocator.

This is the register-level counterpart of
[O0027](O0027-copy-propagation.md), which does the same thing for memory cells.

## Applies to

```basic
DIM a%, b%, t%
t% = a% + b%
PRINT t%
```

## Today

The staging discipline routes values through AX and copies them where the callee
or the next operation wants them:

```asm
    mov     ax, [a]
    add     ax, [b]
    mov     [t], ax
    mov     ax, [t]
    mov     bx, ax           ; the copy the ABI shape forced
    ...
```

## Planned

The consumer is allocated AX directly, so no copy is emitted.

## What it needs

- A real allocator with **interference information** — coalescing is meaningless
  without live ranges, so this is a sub-item of
  [O0058](O0058-386-register-allocation.md).
- The x86-16 wrinkle from [O0072](O0072-register-reassignment.md): the
  accumulator forms are shorter encodings, so coalescing a value *out of* AX can
  grow the code even as it removes an instruction. The cost model
  ([O0174](O0174-target-cost-models.md)) has to decide.
