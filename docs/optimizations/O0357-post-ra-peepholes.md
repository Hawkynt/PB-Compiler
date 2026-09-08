# O0357 — Post-register-allocation peepholes

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | After register allocation |
| **Related** | [O0085](O0085-copy-coalescing.md), [O0034](O0034-redundant-load-elimination.md), [O0356](O0356-machine-combiner.md) |

## The idea

Once **physical** registers are assigned, patterns appear that no earlier pass
could see. Two unrelated virtual registers can become the same physical
register, a copy can become a self-move, or a short save/restore pair can become
an identity.

`Backend/PostRegisterAllocationPeepholes.cs` reads the allocator's final
virtual-to-physical map without introducing another machine representation. It
currently removes:

- `MOV` instructions whose source and destination resolve to the same physical
  register;
- adjacent `PUSH r / POP r` identities;
- copy-back and duplicate-copy pairs exposed by allocation;
- overwritten register/immediate staging moves when the first value is never
  observable.

Byte-sized virtual registers are compared in the same view the emitter uses:
an allocator entry of `AX`, `CX`, `DX` or `BX` becomes `AL`, `CL`, `DL` or `BL`
for a byte operand. This is correctness-critical for the overwritten-copy rule:
a pinned low-byte source must be recognized as reading the destination rather
than as an unrelated register.

The pass deliberately refuses to delete an overwritten **memory load**. PB can
address absolute/far hardware memory, so O0357 does not silently turn a
register-allocation cleanup into a dead-I/O-read optimization.

## Applies to

```asm
    mov     ax, ax           ; two virtual values coalesced to AX
```

which disappears after allocation.

## Safety and limits

- Only adjacent local windows are rewritten.
- Conditional instructions and instructions carrying explicit clobbers are
  excluded.
- Byte-register equality follows the final emitted register (`AX`→`AL`, etc.),
  not merely the allocator's containing word register.
- Memory reads are not deleted by the overwritten-copy rule.
- The pass is optimizer-gated through the same selection marker as the existing
  machine peepholes; `$OPTIMIZE OFF` therefore keeps the faithful stream.
