# O0313 — Parallel prefix scan

| | |
|---|---|
| **Status** | ⬜ Planned — hosted back ends only (see [O0311](O0311-parallel-loop-versioning.md)) |
| **Stage** | Mid-end |
| **Related** | [O0119](O0119-reduction-recognition.md), [O0312](O0312-parallel-reduction.md), [O0134](O0134-recurrence-shortening.md) |

## The idea

A cumulative sum — `t(i) = t(i-1) + a(i)` — looks strictly sequential, but it is
a **scan**, and a scan parallelizes in two passes: each worker computes the
reduction of its slice, the slice offsets are combined, then each worker applies
its offset while computing its local scan.

## Applies to

```basic
DIM i%, t&(0 TO 999999), a&(0 TO 999999)
FOR i% = 1 TO 999999
  t&(i%) = t&(i% - 1) + a&(i%)
NEXT
```

## What it needs

- Recognition that the recurrence is an **associative scan**, not an arbitrary
  loop-carried dependency — the same classifier as
  [O0119](O0119-reduction-recognition.md), with the intermediate results kept.
- Exact wrap semantics per element: the two-pass form must reproduce the
  sequential values including every intermediate wrap.
- Even on a single thread the recognition pays: it is the fact that enables the
  vectorized scan and the closed forms of
  [O0134](O0134-recurrence-shortening.md).
