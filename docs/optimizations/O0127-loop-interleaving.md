# O0127 — Loop interleaving (latency hiding)

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end + scheduler |
| **Related** | [O0038](O0038-instruction-scheduling.md), [O0126](O0126-unroll-and-jam.md), [O0128](O0128-software-pipelining.md) |

## The idea

After unrolling, the copies are usually emitted one after another — load,
compute, store, load, compute, store. Interleaving them separates each load from
its consumer:

```
load  i
load  i+1
compute i
load  i+2
compute i+1
store i
...
```

Every load then has a full iteration's worth of work between issue and use,
which hides its latency without changing what is computed.

## Applies to

```basic
$OPTIMIZE SPEED
DIM i%, a%(0 TO 999), b%(0 TO 999)
FOR i% = 0 TO 999
  b%(i%) = a%(i%) * 3
NEXT
```

## Today

Unrolling ([O0007](O0007-loop-unrolling.md)) emits N sequential copies; the
assembler-level scheduler ([O0038](O0038-instruction-scheduling.md)) can then
reorder within a window, but only if the copies are adjacent, fixup-free and
label-free.

## Planned

The unroller itself emits the interleaved order, so the scheduler starts from a
good arrangement instead of having to discover it.

## What it needs

- Unrolling and scheduling to cooperate: the unroller knows the copies are
  independent, which the byte-level scheduler has to re-derive.
- **A target where loads have latency to hide.** On an 8086 the bus is the
  bottleneck and there is no overlap to win; from the 486 onward there is. This
  is a cost-model decision ([O0174](O0174-target-cost-models.md)).
- Register pressure: interleaving N copies needs N live values at once.
