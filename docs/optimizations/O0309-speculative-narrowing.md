# O0309 — Speculative integer narrowing

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0221](O0221-operation-narrowing.md), [O0057](O0057-storage-narrowing.md), [O0304](O0304-guarded-specialization.md) |

## The idea

[O0221](O0221-operation-narrowing.md) narrows a 32-bit operation when the
lattice proves both operands fit a word. When it cannot prove it, **one range
guard** at the top of a region establishes the same fact for every operation
inside — instead of the repeated per-value checks that would cost more than they
save.

## Applies to

```basic
DIM i%, a&(0 TO 999), s&
FOR i% = 0 TO 999
  s& = s& + a&(i%) * 2       ' 32-bit throughout, though the data is small
NEXT
```

## Planned

```asm
    ; guard: every element is within +/-32767 (or a declared bound)
    ; -> a 16-bit loop; else the general 32-bit one
```

## What it needs

- A cheap source for the guard: a declared range, a preceding clamp, or a
  profile ([O0270](O0270-value-profile-specialization.md)) — scanning the data
  to prove it usually costs more than the narrowing wins.
- The narrowed region must reproduce the **wrap** behaviour of the wide one for
  every input the guard admits, which is exactly the exactness argument
  [O0221](O0221-operation-narrowing.md) already makes for its stricter proof.
