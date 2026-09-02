# O0358 — Late load/store optimization

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | After register allocation/spilling |
| **Related** | [O0065](O0065-dead-frame-store-elimination.md), [O0086](O0086-spill-slot-reuse.md), [O0034](O0034-redundant-load-elimination.md) |

## The idea

Spilling **creates** memory traffic that the mid-end never saw, and some of it is
immediately redundant: a reload when the spilled value is still known in a
physical register, a repeated store of the same value, or a spill overwritten
before anyone reads it.

`Backend/LateLoadStoreOptimization.cs` performs local value forwarding over
allocator-owned `MOperand.StackSlot`s after register allocation. It can replace a
reload by a register/immediate move, remove an exact redundant reload/store, and
delete an unread spill store overwritten by a later one.

The compiler-private region is identified without guessing: when optimized
instruction selection finishes, `MachineOptimizationState` records the current
stack-slot count. Allocation/spilling only appends slots, so indices at or above
that boundary are spill slots. Selector-owned allocas, x87/QUAD staging cells and
other source-level frame storage are therefore outside this pass.

## Safety and limits

- Facts are block-local and die at calls, inline assembly, terminators, unknown
  memory writes, and physical-register clobbers.
- Stack-slot keys include slot, displacement and width.
- Overlapping accesses are tracked by byte range. A partial word load from a
  dword spill marks the whole overlapping earlier store as observed, preventing
  an unsafe later dead-store deletion.
- Only allocator-created slots participate; source/selector frame cells do not.
- The pass is optimizer-gated and runs immediately before emission, after the
  allocator has created the traffic it is meant to remove.
