# O0176 — Register-pressure-aware scheduling and live-range splitting

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Scheduling / register allocation |
| **Related** | [O0175](O0175-critical-path-scheduling.md), [O0058](O0058-386-register-allocation.md), [O0087](O0087-rematerialization.md) |
| **Split into** | [O0264](O0264-live-range-splitting.md), [O0265](O0265-vector-lane-coalescing.md) |

## The idea

Scheduling and allocation pull in opposite directions: hoisting a load earlier
hides its latency but **lengthens its live range**, and a live range that
outlives the register file becomes a spill — which costs more than the latency
it saved. Three coupled decisions:

- **pressure-aware scheduling** — do not move a definition so far from its use
  that the allocator runs out of registers;
- **live-range splitting around calls** — spill or rematerialize only the part
  of a value's lifetime that crosses a call, instead of the whole range;
- **vector-lane coalescing** — assign related vector values to registers that
  minimize shuffles and moves.

## Applies to

```basic
$OPTIMIZE SPEED
DIM i%, a%(0 TO 99), b%(0 TO 99), c%(0 TO 99), s%
FOR i% = 0 TO 99
  s% = s% + a%(i%) + b%(i%) + c%(i%)      ' three loads, one accumulator, two registers
NEXT
```

## Today

The 8086 tier has exactly two allocatable registers, so the question barely
arises — the pressure is always maximal and the heuristics are tuned for it.

## Planned

On a 386+ with six registers, the scheduler and the allocator negotiate: loads
issue early while registers are available, and the accumulator's range is split
around anything that would otherwise force it out.

## What it needs

- An allocator to negotiate with ([O0058](O0058-386-register-allocation.md)).
- A pressure estimate at each program point, which the SSA liveness already
  provides in outline.
- [O0087](O0087-rematerialization.md) as the cheaper alternative to splitting
  wherever the value is trivially recomputable.
