# O0334 — Binary-search recognition

| | |
|---|---|
| **Status** | 🟡 Partial — canonical counted searches over sorted unique read-only 8/16-bit integer tables become balanced binary-search CFGs |
| **Stage** | Mid-end |
| **Source** | `Ir/Passes/StaticSearchRecognition.cs` |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` |
| **Verified by** | `StaticDispatchOptimizationTests` |
| **Related** | [O0073](O0073-algorithmic-idiom-catalog.md), [O0098](O0098-balanced-decision-tree.md), [O0335](O0335-perfect-hash-data.md) |

## The idea

A linear scan over **compile-time-sorted constant data** is O(n) for no reason:
the compiler knows the data is sorted, because it emitted it. Replacing the scan
with a binary search is a pure algorithmic upgrade.

## Implemented v1

`StaticSearchRecognition` matches a canonical zero-based counted loop whose body
loads one element from a constant read-only integer table, compares it for
equality with a key, and returns the first matching index (or the existing
failure result). For sorted unique sets of at least eight keys it emits an
acyclic balanced decision tree.

The ordering predicate follows the table element's signedness, even when the
search key uses a storage-compatible signed/unsigned IR type. Duplicate keys are
rejected because first-match semantics would otherwise need extra handling.

## Applies to

```basic
DIM keys%(0 TO 99), i%
DATA 3, 17, 42, 56, 91, ...        ' sorted constants
FOR i% = 0 TO 99
  IF keys%(i%) = k% THEN EXIT FOR
NEXT
```

when lowering produces the canonical counted-search form and the table is
materialized as read-only constant data.

## Still planned

- Less canonical loops, alternative failure conventions and searches that store
  the result rather than returning it directly.
- String/fixed-record key searches.
- Target-aware thresholds comparing linear scan, binary tree and hashed forms.
- Shared analysis with readonly-global/range passes for more source shapes.
