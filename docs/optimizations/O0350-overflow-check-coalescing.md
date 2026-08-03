# O0350 — Overflow-check coalescing

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0219](O0219-overflow-check-elimination.md), [O0308](O0308-speculative-overflow-elimination.md), [O0117](O0117-bounds-check-merging.md) |

## The idea

A chain of checked operations emits a `JNO` after each one. When the operations
share operands, **one** range guard on the inputs can replace the whole chain:
prove the inputs small enough and no intermediate can overflow.

## Applies to

```basic
$ERROR OVERFLOW ON
DIM a%, b%, c%, r%
r% = a% + b% + c%            ' two adds, two JNO guards
```

If `a%`, `b%` and `c%` are each within ±10 000, neither sum can overflow, and one
test on the widest input replaces both guards.

## What it needs

- The interval arithmetic of [O0016](O0016-value-fact-analysis.md) applied to
  the *precondition* rather than to each operation.
- **Which** operation would have overflowed first must still be preserved: under
  `$ERROR OVERFLOW` the error is raised at a specific statement, and a coalesced
  guard that fires earlier reports a different one. So the guard must be
  *sufficient*, and the checked chain must remain on the failing path
  ([O0304](O0304-guarded-specialization.md)).
