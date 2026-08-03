# O0334 — Binary-search recognition

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end |
| **Related** | [O0073](O0073-algorithmic-idiom-catalog.md), [O0098](O0098-balanced-decision-tree.md), [O0335](O0335-perfect-hash-data.md) |

## The idea

A linear scan over **compile-time-sorted constant data** is O(n) for no reason:
the compiler knows the data is sorted, because it emitted it. Replacing the scan
with a binary search is a pure algorithmic upgrade — and where the keys are a
small constant set, a decision tree or perfect hash is better still.

## Applies to

```basic
DIM keys%(0 TO 99), i%, found%
DATA 3, 17, 42, 56, 91, ...        ' sorted constants
FOR i% = 0 TO 99
  IF keys%(i%) = k% THEN found% = i% : EXIT FOR
NEXT
```

## What it needs

- Proof that the array is **initialized from constants and never written** —
  which is [O0165](O0165-readonly-global-propagation.md)'s analysis applied to an
  array, plus a sortedness check the compiler performs on the constant data.
- The loop must be a plain search (no side effects in the body), and its
  observable result — the found index, or the counter's end value on failure —
  must be reproduced exactly.
- Below a threshold the linear scan is faster; above it, and especially for a
  constant key set, [O0335](O0335-perfect-hash-data.md) beats both.
