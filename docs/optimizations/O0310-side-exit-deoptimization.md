# O0310 — Side exits and deoptimization

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end + emitter |
| **Related** | [O0304](O0304-guarded-specialization.md), [O0306](O0306-loop-versioning.md), [O0308](O0308-speculative-overflow-elimination.md) |

## The idea

Enter optimized code under an assumption and **exit to generic code** the moment
it fails — mid-loop, not only at the entry guard. That allows assumptions which
hold for most of an execution but not all of it: a narrow range that one late
element violates, an alias that appears only on the last iteration.

The side exit must reconstruct the generic version's state: the counter, the
accumulators, the resident registers flushed to their cells.

## Applies to

```basic
FOR i% = 0 TO n%
  s% = s% + a%(i%)           ' fast 16-bit path until a value exceeds the range
NEXT
```

## What it needs

- A **state map** at each exit point: which optimized value corresponds to which
  variable, so the generic loop can be re-entered at the right iteration with the
  right values. This is the piece that makes deoptimization real work rather than
  a branch.
- Careful accounting of side effects already performed on the fast path — a
  half-executed iteration must not be repeated.
- It is the most speculative item in the list, and the honest note is that
  entry-guarded versioning ([O0306](O0306-loop-versioning.md)) captures most of
  the benefit for a fraction of the machinery.
