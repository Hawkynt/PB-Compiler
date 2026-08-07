# O0186 — CSE reuse through loop preheaders

| | |
|---|---|
| **Status** | ✅ Implemented (2026-07) |
| **Stage** | Pre-emission analysis |
| **Source** | `CodeGen/OptCommonSubexpr.cs` |
| **Gate** | `--optimize` |
| **IR** | ✅ Falls out of SSA + `Gvn` — the preheader dominates the body, so a value computed before the loop is still the leader inside it. `Licm` covers the other direction (hoisting an invariant OUT); this is reuse of one already there. Verified by `CseShapeTests` |
| **Split from** | [O0003](O0003-common-subexpression-elimination.md) |

## What it is

A value computed **before** a `FOR`/`DO` loop whose body never writes its inputs
is inherited *into* the body — every iteration reloads the pre-loop slot — and
survives *past* the loop, so the zero-trip path falls through with the same
survivors.

This is distinct from [O0028](O0028-loop-invariant-code-motion.md): LICM
*hoists* a computation into the preheader, while this reuses one that was
already there.

## Sample

```basic
DIM w%, h%, i%, t%, u%
t% = w% * h%                 ' computed before the loop
FOR i% = 0 TO 99
  a%(i%) = t% + w% * h%      ' reloads the slot instead of recomputing
NEXT
u% = w% * h%                 ' still valid after the loop
```

## Why it is safe

The body's writes and the `FOR` counter are invalidated **up front**, not
incrementally — a pass-N write must also kill the value for pass N+1's start.
The body must be retainable and call-free by the same test
[O0185](O0185-cse-past-merge.md) uses.
