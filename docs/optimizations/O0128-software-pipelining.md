# O0128 — Software pipelining and modulo scheduling

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Scheduler |
| **Related** | [O0127](O0127-loop-interleaving.md), [O0038](O0038-instruction-scheduling.md), [O0174](O0174-target-cost-models.md) |
| **Split into** | [O0267](O0267-modulo-scheduling.md) |

## The idea

Restructure a loop so that different **iterations** occupy different pipeline
stages at the same time: a prologue that starts the first iterations, a
steady-state kernel where iteration *n* stores while *n+1* computes and *n+2*
loads, and an epilogue that drains.

**Modulo scheduling** is the general form: choose an initiation interval II
(one new logical iteration every II cycles) and schedule the body so that no
resource is oversubscribed modulo II.

## Applies to

```basic
$OPTIMIZE SPEED
DIM i%, a%(0 TO 999), b%(0 TO 999), c%(0 TO 999)
FOR i% = 0 TO 999
  c%(i%) = a%(i%) * b%(i%)
NEXT
```

## Planned (schematically)

```
prologue:   load a0,b0
kernel:     load a(n+1),b(n+1) ; mul n ; store c(n-1)
epilogue:   mul 999 ; store c(998) ; store c(999)
```

## What it needs

- A **resource model** (which units, how many, what latency) — that is the whole
  content of the transformation, and it is per target
  ([O0174](O0174-target-cost-models.md)). On an 8086 there is one execution unit
  and one bus unit, so the achievable II is trivially bounded and the payoff is
  small; on a superscalar target it is large.
- Registers for the in-flight values (each stage holds its own copy), so it is
  downstream of [O0058](O0058-386-register-allocation.md).
- Correct prologue/epilogue generation for **any** trip count, including the
  degenerate ones where the pipeline never fills — plus PB's exact post-loop
  counter value.
