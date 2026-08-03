# O0358 — Late load/store optimization

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | After register allocation |
| **Related** | [O0065](O0065-dead-frame-store-elimination.md), [O0086](O0086-spill-slot-reuse.md), [O0034](O0034-redundant-load-elimination.md) |

## The idea

Spilling **creates** memory traffic that the mid-end never saw, and some of it is
immediately redundant: a spill followed by a reload with nothing in between, two
spills of the same value to different slots, a reload of a value that is still
live in another register.

Removing it requires knowing which cells are compiler-private, which is exactly
the information [O0065](O0065-dead-frame-store-elimination.md) is blocked on.

## Applies to

```asm
    mov     [bp-8], ax       ; spill
    ...                      ; a region that never reads [bp-8]
    mov     ax, [bp-8]       ; reload — both are dead if AX survived
```

## What it needs

- The code generator to **declare the compiler-temp region** of the frame, so a
  whole-procedure "no other reader" scan is sound — the unblocking step named in
  [O0065](O0065-dead-frame-store-elimination.md).
- Complete instruction recording, including the forms that currently escape it
  (`LEA`, `PUSH mem`, read-modify-write ALU ops, indirect jumps).
