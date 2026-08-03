# O0113 — Loop bounds loaded once

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0028](O0028-loop-invariant-code-motion.md), [O0005](O0005-register-residency.md), [O0131](O0131-exact-trip-count.md) |

## The idea

A `FOR` loop's limit and step are evaluated once by definition — PB evaluates
them at loop entry — but the *values* are kept in frame cells and reloaded for
the compare on every iteration. Holding them in a register (or folding the limit
into the compare as an immediate when it is constant) removes a memory access
per iteration.

## Applies to

```basic
DIM i%, n%, s%
n% = 1000
FOR i% = 1 TO n%
  s% = s% + i%
NEXT
```

## Today

```asm
Top:
    mov     ax, si
    cmp     ax, [bp-limit]   ; reloaded every iteration
    jg      Done
```

## Planned

```asm
    mov     bx, [bp-limit]   ; once, in the preheader
Top:
    cmp     si, bx
    jg      Done
```

or, when the limit is a constant, `CMP SI,03E8h` with no cell at all — which the
int16 fast path already does for literal bounds.

## What it needs

- A free register at the loop level. On an 8086 SI and DI are already spoken for
  by [O0005](O0005-register-residency.md), so the limit competes with the
  accumulator — another case for the cost model
  ([O0174](O0174-target-cost-models.md)). On a 386 there is room
  ([O0058](O0058-386-register-allocation.md)).
- The limit cell must still be written if anything else can observe it (it is a
  compiler temp, so normally nothing can).
