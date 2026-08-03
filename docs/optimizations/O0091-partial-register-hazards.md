# O0091 — Partial-register hazards and false-dependency breaking

| | |
|---|---|
| **Status** | ⬜ Planned (target-dependent; irrelevant on the 8086, decisive from the P6 onward) |
| **Stage** | Emitter / register allocation |
| **Related** | [O0058](O0058-386-register-allocation.md), [O0174](O0174-target-cost-models.md), [O0008](O0008-peephole-zero-idiom.md) |

## The idea

Two microarchitectural rules that only exist on the later targets:

1. **Partial-register hazards.** Writing `AL` and then reading `AX` stalls on a
   P6-class core (the partial write must be merged). Mixing byte, word and
   32-bit writes to the same architectural register should be avoided where a
   full-width write would do.
2. **False-dependency breaking.** `XOR EAX,EAX` is recognized by the processor
   as *independent* of EAX's previous value, whereas `MOV EAX,0` is not on some
   cores — and conversely, some byte-width writes create a dependency on the
   full register that a zeroing idiom removes.

Note the direction of the tension with the 8-bit sub-register packing idea in
[O0058](O0058-386-register-allocation.md): packing two `BYTE` locals into
`DL`/`DH` is a **win on an 8086** (registers are scarce, no hazard exists) and a
**stall on a P6**. Only a per-target cost model can hold both facts at once.

## Applies to

```basic
$CPU 80586
DIM lo AS BYTE, hi AS BYTE, w AS WORD
lo = 1 : hi = 2
w = lo + hi * 256
```

## What it needs

- [O0174](O0174-target-cost-models.md) — the hazard table is per
  microarchitecture, and the 8086/186/286/386 targets must not pay any of its
  costs.
- The allocator has to know register *widths*, not just register identities,
  which the current SI/DI residency model does not need to.
