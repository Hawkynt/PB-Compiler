# O0264 — Live-range splitting around calls

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Register allocation |
| **Related** | [O0176](O0176-register-pressure-scheduling.md), [O0058](O0058-386-register-allocation.md), [O0087](O0087-rematerialization.md) |
| **Split from** | [O0176](O0176-register-pressure-scheduling.md) |

## The idea

A value that is live across a call currently loses its register for its **entire**
lifetime, because the calling convention lets the callee clobber it. Splitting
the range means keeping it in a register before and after the call and spilling
only the crossing part — or rematerializing it afterwards
([O0087](O0087-rematerialization.md)).

## Applies to

```basic
DIM i%, acc%, a%(0 TO 99)
FOR i% = 0 TO 99
  acc% = acc% + a%(i%)
  CALL Report(i%)            ' the call is why acc% cannot stay resident today
NEXT
```

## What it needs

- An allocator with live ranges to split ([O0058](O0058-386-register-allocation.md)).
- The call ABI's clobber set — and on 8086 the observation that **SI/DI are
  callee-owned scratch**, which is precisely why cross-call residency is
  impossible there and why this item belongs to the 386 tier.
- Interaction with `ON ERROR`: a handler re-entering the frame must see memory,
  so a split range has to be flushed at any point a fault can be taken.
