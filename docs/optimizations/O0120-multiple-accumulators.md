# O0120 — Multiple accumulators break the dependency chain

| | |
|---|---|
| **Status** | ⬜ Planned (no benefit before a pipelined target) |
| **Stage** | Mid-end |
| **Related** | [O0119](O0119-reduction-recognition.md), [O0121](O0121-reduction-tree-balancing.md), [O0126](O0126-unroll-and-jam.md) |

## The idea

`sum = sum + a(i)` is a serial dependency: iteration *n+1*'s add cannot start
until iteration *n*'s finishes. Splitting the reduction into several independent
accumulators, combined once at the end, breaks the chain — each partial sum
progresses independently.

```basic
s0 = s0 + a(i)   : s1 = s1 + a(i+1)
s2 = s2 + a(i+2) : s3 = s3 + a(i+3)
' ... then s = s0 + s1 + s2 + s3
```

## Applies to

```basic
$OPTIMIZE SPEED
DIM i%, s%, a%(0 TO 999)
FOR i% = 0 TO 999
  s% = s% + a%(i%)
NEXT
```

## Today

One accumulator in DI, one add per iteration — which is optimal on an 8086,
where the add is not pipelined and nothing overlaps.

## Planned (target-gated)

Four accumulators on a Pentium-class target, where four independent adds issue
in the latency of one chain; a single final reduction restores the value.

## What it needs

- [O0119](O0119-reduction-recognition.md) to classify the reduction, and
  unrolling to expose the independent operations.
- **Registers to hold them**, which on an 8086 do not exist — so this is gated
  on [O0058](O0058-386-register-allocation.md) and on a target where the split
  pays at all ([O0174](O0174-target-cost-models.md)). On an 8086 it is strictly
  a loss.
- Exact wrap semantics: splitting a sum is legal because addition is associative
  modulo 2ⁿ; it is **not** legal for a float reduction, where the rounding
  differs.
