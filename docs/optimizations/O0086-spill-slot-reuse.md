# O0086 — Spill-slot reuse

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Frame layout |
| **Related** | [O0003](O0003-common-subexpression-elimination.md), [O0019](O0019-zero-elision.md), [O0065](O0065-dead-frame-store-elimination.md) |

## The idea

Compiler temporaries — CSE slots, argument staging cells, spill locations — each
get their own frame slot today. Temporaries whose live ranges do not overlap can
share one slot, which shrinks the frame, shortens the prologue's zero fill
([O0019](O0019-zero-elision.md)) and keeps more of the frame inside the short
`[BP-disp8]` addressing form.

## Applies to

```basic
DIM x%, y%, a%, b%, c%, d%
a% = (x% + y%) * 2          ' CSE slot #1, dead after this statement
PRINT a%
b% = (x% - y%) * 3          ' could reuse slot #1
PRINT b%
```

## Today

```
frame: [bp-2] a  [bp-4] b  [bp-6] cse1  [bp-8] cse2 ...
```

## Planned

```
frame: [bp-2] a  [bp-4] b  [bp-6] cse1+cse2 (shared)
```

## What it needs

- **Live ranges for temporaries**, which the CSE analysis already computes in
  outline (define → last reload) but does not currently expose as an interval
  set for packing.
- A guarantee that a shared slot is never observed across a barrier that could
  reach both users — the same invalidation set the CSE cache uses.
- Interaction with [O0019](O0019-zero-elision.md): a smaller frame is also a
  cheaper `REP STOSW` in the cases where the fill survives.
