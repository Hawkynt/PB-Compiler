# O0265 — Vector lane register coalescing

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Register allocation |
| **Related** | [O0176](O0176-register-pressure-scheduling.md), [O0143](O0143-slp-vectorization.md), [O0144](O0144-interleaved-access-vectorization.md) |
| **Split from** | [O0176](O0176-register-pressure-scheduling.md) |

## The idea

Vector code pays for **data movement between lanes**: a shuffle to bring
operands into matching positions, a move to satisfy a two-operand instruction's
destination. Assigning related vector values to registers whose lane layout
already matches removes those shuffles.

## Applies to

```basic
$CPU 80586 SSE
FOR i% = 0 TO 999
  c%(i%) = a%(i%) * b%(i%) + d%(i%)
NEXT
```

Here the multiply's destination and the add's first operand want to be the same
register, or a `MOVDQA` appears between them.

## What it needs

- Lane-position tracking in the allocator, not just register identity —
  which is a genuinely different allocation problem from the scalar one.
- Awareness of the two-operand MMX/SSE forms (`dest = dest OP src`) versus the
  three-operand VEX forms, where the constraint disappears entirely.
