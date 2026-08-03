# O0170 — Leaf register save/restore elision

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0070](O0070-leaf-frame-elision.md), [O0005](O0005-register-residency.md), [O0058](O0058-386-register-allocation.md) |

## The idea

A procedure saves and restores the callee-stable registers (SI, DI, BP) because
it *might* modify them. A leaf procedure whose selected code demonstrably does
not modify a given register should not save it.

On an 8086 that is up to three pushes and three pops per call — for a small leaf
procedure, a substantial fraction of its total cost.

The same reasoning inverts for the caller: in a leaf or fully-inlined region,
the allocator should **prefer caller-saved registers**, because using a
callee-saved one forces the save/restore pair that the elision is trying to
avoid.

## Applies to

```basic
FUNCTION Add3%(BYVAL a%, BYVAL b%, BYVAL c%)
  Add3% = a% + b% + c%       ' touches only AX
END FUNCTION
```

## Today

```asm
Add3:
    push    bp
    mov     bp, sp
    push    si               ; saved unconditionally
    push    di
    ...
    pop     di
    pop     si
    mov     sp, bp
    pop     bp
    ret     6
```

## Planned

```asm
Add3:
    mov     ax, [sp+2]
    add     ax, [sp+4]
    add     ax, [sp+6]
    ret     6
```

(with [O0070](O0070-leaf-frame-elision.md) removing the frame as well, and
[O0021](O0021-register-parameters.md) the stack traffic).

## What it needs

- A per-procedure **clobber set** computed after instruction selection — which
  the emitter can produce, since it knows every instruction it emitted.
- Care with the register-residency passes, which deliberately *do* use SI/DI:
  the elision must run after residency is decided, not before.
