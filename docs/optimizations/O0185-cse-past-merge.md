# O0185 — CSE retention past a merge

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Pre-emission analysis |
| **Source** | `CodeGen/OptCommonSubexpr.cs` — `RetainPastMerge`, `IsRetainableBranch`, `CollectWrites` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF67.BAS` (IF), `DIFF68.BAS` (`SELECT CASE`) |
| **Split from** | [O0003](O0003-common-subexpression-elimination.md) |

## What it is

A cached value normally dies at a control-flow merge, because either arm might
have written its inputs. `RetainPastMerge` keeps the entry alive **through** the
join when no branch can have overwritten those inputs — so a value computed
before an `IF` and reused *after* it reloads as well.

## Sample

```basic
DIM x%, y%, a%, b%
a% = y% * 320 + x%
IF flag% THEN c% = 1 ELSE c% = 2
b% = y% * 320 + x%           ' still valid: neither arm wrote x% or y%
```

## Why it is safe

Sound only when every arm is a flat, call-free straight line
(`IsRetainableBranch`), so `CollectWrites` captures the exact write set. Nested
control flow or a call falls back to the conservative clear of the whole cache.

The same treatment covers `SELECT CASE` joins (barrier-free subject and
selectors), where a value flows into the arms and past the merge exactly as for
an `IF`.
