# O0246 — Move-elimination-aware allocation

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Register allocation |
| **Related** | [O0085](O0085-copy-coalescing.md), [O0092](O0092-encoding-selection.md), [O0174](O0174-target-cost-models.md) |
| **Split from** | [O0092](O0092-encoding-selection.md) |

## The idea

Some cores resolve register-to-register moves in the **rename** stage, at zero
execution cost. Where that holds, a `MOV BX,AX` is nearly free and coalescing it
away ([O0085](O0085-copy-coalescing.md)) buys only code size — while on an 8086
the same move costs a real cycle and two bytes.

The allocator should therefore know whether moves are free on the target before
it trades register assignments to remove them.

## What it needs

- A "move elimination" flag and its conditions (which register classes, which
  forms) per target in [O0174](O0174-target-cost-models.md).
- The x86-16 counter-consideration from
  [O0072](O0072-register-reassignment.md): moving a value **out of AX** lengthens
  the encoding, so on this target the trade can be negative in both directions.
