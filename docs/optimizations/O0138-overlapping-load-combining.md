# O0138 — Overlapping load combining

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0136](O0136-adjacent-access-merging.md), [O0137](O0137-load-widening.md), [O0047](O0047-ir-redundant-memory.md) |

## The idea

Repeated loads from **overlapping** addresses — `a(i)` and `a(i+1)`, or two byte
reads one apart — can be served by one wider load plus a shift or a mask. The
classic case is a sliding window: a filter that reads each element twice, once
as "current" and once as "previous".

## Applies to

```basic
DIM i%, a%(0 TO 999), d%(0 TO 999)
FOR i% = 1 TO 999
  d%(i%) = a%(i%) - a%(i% - 1)      ' every element is read twice
NEXT
```

## Today

Two loads per iteration, 1 998 loads for 1 000 elements.

## Planned

The previous element is already in a register from the last iteration — the
loop carries it forward:

```asm
    mov     dx, [bx]         ; a(i-1), carried from the previous iteration
Top:
    add     bx, 2
    mov     ax, [bx]         ; a(i) — one load
    mov     cx, ax
    sub     ax, dx
    mov     [di], ax
    mov     dx, cx           ; becomes a(i-1) for the next pass
```

One load per iteration instead of two.

## What it needs

- **Loop-carried value forwarding**: recognizing that a load's address in
  iteration *n* equals another load's address in iteration *n−1*, which is an
  induction-variable fact ([O0110](O0110-general-induction-variables.md)).
- A register to carry the value, and the prologue that primes it before the
  first iteration ([O0115](O0115-loop-peeling.md)).
- Alias safety: nothing in the body may write the array between the two reads
  ([O0060](O0060-memory-ssa.md)).
