# O0312 — Parallel reduction

| | |
|---|---|
| **Status** | ⬜ Planned — hosted back ends only (see [O0311](O0311-parallel-loop-versioning.md)) |
| **Stage** | Mid-end |
| **Related** | [O0119](O0119-reduction-recognition.md), [O0120](O0120-multiple-accumulators.md), [O0311](O0311-parallel-loop-versioning.md) |

## The idea

Each worker keeps a **private accumulator** over its slice, and the partial
results are combined at the end — the thread-level form of the vector reduction
([O0145](O0145-vector-reduction.md)) and of multiple accumulators
([O0120](O0120-multiple-accumulators.md)).

## Applies to

```basic
DIM i%, s&, a&(0 TO 999999)
FOR i% = 0 TO 999999
  s& = s& + a&(i%)
NEXT
```

## What it needs

- The reduction classification ([O0119](O0119-reduction-recognition.md)) and its
  identity element.
- **Associativity**: exact for the integer operators (modulo 2ⁿ), *not* for
  floats — a parallel float sum changes the rounding and therefore the printed
  result, which for this compiler is a correctness failure, not a tolerance
  question.
- Per-worker accumulator padding to avoid false sharing
  ([O0317](O0317-false-sharing-avoidance.md)).
