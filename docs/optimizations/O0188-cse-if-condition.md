# O0188 — `IF`-condition subexpression caching

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Pre-emission analysis |
| **Source** | `CodeGen/OptCommonSubexpr.cs` |
| **Gate** | `--optimize` |
| **IR** | ✅ Falls out of SSA + `Gvn` — the test dominates both arms, so a condition recomputed inside the arm it guards is numbered to the test. Verified by `CseShapeTests` |
| **Split from** | [O0003](O0003-common-subexpression-elimination.md) |

## What it is

The condition of an `IF` is evaluated **unconditionally** and dominates every
arm, so its subexpressions are cacheable like any others. Registering them is
what lets an arm reuse a value the test just computed.

Only the **first** condition qualifies: an `ELSEIF`'s condition runs only when
the preceding ones were false, so it does not dominate the arms below it.

## Sample

```basic
DIM a%(0 TO 99), i%, m%
IF a%(i%) > m% THEN m% = a%(i%)
```

The element read inside the condition defines the slot; the assignment in the
arm reloads it ([O0187](O0187-redundant-array-load.md)).

## Why it is safe

Dominance is the entire argument: a value computed in the condition has been
computed on every path that reaches an arm. The `ELSEIF` restriction is exactly
where that argument stops holding.
