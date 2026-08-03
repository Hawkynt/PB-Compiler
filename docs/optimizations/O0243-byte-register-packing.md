# O0243 — 8-bit sub-register packing

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Register allocation |
| **Related** | [O0058](O0058-386-register-allocation.md), [O0091](O0091-partial-register-hazards.md), [O0174](O0174-target-cost-models.md) |
| **Split from** | [O0058](O0058-386-register-allocation.md) |

## The idea

Two non-escaping `BYTE` locals share one 16-bit register's halves — `DL`/`DH`,
`BL`/`BH` — doubling the effective register count for byte-heavy code on a
machine that has almost none.

## Applies to

```basic
$OPTIMIZE SPEED
SUB Blit
  LOCAL fg AS BYTE, bg AS BYTE, i%
  FOR i% = 0 TO 999
    ...                      ' both bytes live across the loop
  NEXT
END SUB
```

## What it needs

- The allocator must prove **neither half is clobbered** by an operation that
  writes the whole 16-bit register: `MUL`/`DIV` and the string operations touch
  AX, address arithmetic touches BX/SI/DI. So AX's halves are poor candidates
  while BX and DX halves work.
- Best done **inside** the allocator, which already tracks per-register
  liveness, rather than as a separate pass.
- **Target-dependent profitability**: this is a clear win on an 8086, where
  registers are scarce and there is no penalty, and a partial-register **stall**
  on a P6-class core ([O0091](O0091-partial-register-hazards.md)). Exactly the
  case the cost model exists for.
