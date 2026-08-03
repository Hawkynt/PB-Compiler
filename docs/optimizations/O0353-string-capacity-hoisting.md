# O0353 — String capacity check hoisting

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end + runtime |
| **Related** | [O0294](O0294-string-builder-recognition.md), [O0208](O0208-inplace-literal-append.md), [O0292](O0292-ownership-batching.md) |

## The idea

Every append checks whether the block can grow — the topmost-block test and the
`$STRING` cap check in `rt_strcatlit`/`rt_strcatvar`. When the final size is
known or boundable in advance, **reserving once** before the loop removes the
per-append check *and* guarantees the in-place path takes effect every time.

## Applies to

```basic
DIM i%, out$
FOR i% = 1 TO 1000
  out$ = out$ + "x"          ' 1 000 capacity checks for a known 1 000 bytes
NEXT
```

## What it needs

- A **capacity concept** in the string manager (length ≠ allocated size), which
  it does not have today — the same prerequisite
  [O0294](O0294-string-builder-recognition.md) names, and the single change that
  unlocks both.
- A size bound from the loop's trip count and the appended lengths
  ([O0131](O0131-exact-trip-count.md)).
- The reservation must be **unobservable**: `LEN` reports the logical length,
  `FRE` accounting stays consistent, and an early exit leaves no over-long
  string.
