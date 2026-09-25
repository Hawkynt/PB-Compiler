# O0185 — CSE retention past a merge

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR middle end |
| **Source** | `Ir/Passes/Gvn.cs` (after `Ir/Passes/Mem2Reg.cs` has put the scalars in SSA form) — no pass of its own |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF67.BAS` (IF), `DIFF68.BAS` (`SELECT CASE`), `CseShapeTests` |
| **Split from** | [O0003](O0003-common-subexpression-elimination.md) |

## What it is

A value computed before an `IF` and recomputed *after* it is reused across the
join when no arm can have changed its inputs. There is no dedicated
bookkeeping for this: in SSA an operand that is still the same SSA value after
the merge *is* the proof that no arm overwrote it, and the block before the
branch dominates the merge, so `Gvn`'s dominator-scoped table still holds the
leader there.

## Sample

```basic
DIM x%, y%, a%, b%
a% = y% * 320 + x%
IF flag% THEN c% = 1 ELSE c% = 2
b% = y% * 320 + x%           ' still valid: neither arm wrote x% or y%
```

## Why it is safe

If an arm does write an input, the merge gets a phi for it and the later
occurrence has a different operand, so it is not congruent. Loads are matched
only when Memory SSA gives both the same memory version, so a store or call in
an arm that may touch the cell keeps the second read.

The same holds for `SELECT CASE` joins: the block before the dispatch dominates
the arms and the merge, so a value flows into the arms and past the merge
exactly as for an `IF`.
