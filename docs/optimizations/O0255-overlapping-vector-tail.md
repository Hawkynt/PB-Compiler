# O0255 — Overlapping final vector

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0146](O0146-vector-tail.md), [O0252](O0252-safe-overread-versioning.md) |
| **Split from** | [O0146](O0146-vector-tail.md) |

## The idea

Process the last full vector's worth of elements *including some already
processed ones*, so one extra vector iteration replaces the entire tail. The
overlap re-computes a few elements rather than running a scalar loop.

## Applies to

```basic
' 10 elements, 4 lanes: vectors at 0-3, 4-7, and 6-9 (overlapping 6-7)
FOR i% = 0 TO 9
  c%(i%) = a%(i%) + b%(i%)
NEXT
```

## What it needs

- **Idempotence** for the overlapped elements: `c(i) = a(i) + b(i)` recomputes
  the same value, but `c(i) = c(i) + 1` does not — so the transform is legal only
  for bodies whose output does not depend on its own previous output.
- The array must be long enough for one full vector; shorter loops keep the
  scalar path.
- No `$ERROR` checking, since an element would be checked twice.
