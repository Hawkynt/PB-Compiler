# O0188 — `IF`-condition subexpression caching

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR middle end |
| **Source** | `Ir/Passes/Gvn.cs` — no pass of its own |
| **Gate** | `--optimize` |
| **Verified by** | `CseShapeTests` |
| **Split from** | [O0003](O0003-common-subexpression-elimination.md) |

## What it is

The condition of an `IF` is evaluated **unconditionally** and its block
dominates every arm, so `Gvn` numbers a subexpression recomputed inside an arm
to the one the test just computed.

An `ELSEIF`'s condition runs only when the preceding ones were false, so its
block dominates only the arms after it; a value it computes is reused there and
nowhere else.

## Sample

```basic
DIM a%(0 TO 99), i%, m%
IF a%(i%) > m% THEN m% = a%(i%)
```

The element read inside the condition is the leader; the read in the arm is
replaced by it ([O0187](O0187-redundant-array-load.md)).

## Why it is safe

Dominance is the entire argument: a value computed in the condition has been
computed on every path that reaches an arm. `Gvn`'s table is scoped to the
dominator tree, so a value is never reused where it does not dominate.
