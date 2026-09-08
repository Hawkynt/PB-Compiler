# O0334 — Binary-search recognition

| | |
|---|---|
| **Status** | 🟡 Partial — counted searches over sorted unique read-only 8/16-bit integer tables become balanced binary-search CFGs, including promoted stored-result forms |
| **Stage** | Mid-end |
| **Source** | `Ir/Passes/StaticSearchRecognition.cs` |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` |
| **Verified by** | `StaticDispatchOptimizationTests` |
| **Related** | [O0073](O0073-algorithmic-idiom-catalog.md), [O0098](O0098-balanced-decision-tree.md), [O0335](O0335-perfect-hash-data.md) |

## The idea

A linear scan over **compile-time-sorted constant data** is O(n) for no reason:
the compiler knows the data is sorted, because it emitted it. Replacing the scan
with a binary search is a pure algorithmic upgrade.

## Implemented

`StaticSearchRecognition` matches a zero-based counted loop whose body loads one
element from a constant read-only integer table and compares it for equality
with a loop-invariant key. Both the direct `EQ -> found` form and the canonical
inverted `NE -> continue` form are accepted.

A search may either return the matching index directly or feed it into the
single result phi produced when an assigned result variable has been promoted by
`mem2reg`. In the latter form the rewrite creates explicit hit/failure
predecessors for that phi, so the original failure value and any continuation
after the search are preserved while the loop blocks disappear.

For sorted unique sets of at least eight keys the pass emits an acyclic balanced
decision tree. The ordering predicate follows the table element's signedness,
even when the search key uses a storage-compatible signed/unsigned IR type.
Duplicate keys are rejected because first-match semantics would otherwise need
extra handling.

The matcher also requires a single-entry loop region and rejects keys defined
inside the region. Those are correctness conditions for deleting the original
loop: no outside edge or surviving SSA use may point into blocks removed by the
rewrite.

## Applies to

```basic
DIM keys%(0 TO 99), i%, result%
DATA 3, 17, 42, 56, 91, ...        ' sorted constants
result% = -1
FOR i% = 0 TO 99
  IF keys%(i%) = k% THEN
    result% = i%
    EXIT FOR
  END IF
NEXT
```

when lowering produces the counted-search form, the table is materialized as
read-only constant data, and the result assignment has either become a promoted
phi or a direct return.

## Still planned

- More induction-variable shapes than zero-based unit-step counted loops.
- String/fixed-record key searches.
- Target-aware thresholds comparing linear scan, binary tree and hashed forms.
- Shared analysis with readonly-global/range passes for more source shapes.
