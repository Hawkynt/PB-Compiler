# O0186 — CSE reuse through loop preheaders

| | |
|---|---|
| **Status** | ✅ Implemented (2026-07) |
| **Stage** | IR middle end |
| **Source** | `Ir/Passes/Gvn.cs` — no pass of its own; `Ir/Passes/Licm.cs` covers the hoisting direction |
| **Gate** | `--optimize` |
| **Verified by** | `CseShapeTests` |
| **Split from** | [O0003](O0003-common-subexpression-elimination.md) |

## What it is

A value computed **before** a `FOR`/`DO` loop whose body never writes its inputs
is reused *inside* the body and *after* the loop: the preheader dominates both,
so `Gvn` replaces the later occurrences with the value computed before the
loop.

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

A variable the body writes gets a loop-header phi, so an expression over it
inside the loop has a different operand from the pre-loop one and is not
congruent — the pass-N write is seen at pass N+1's start by construction. Loads
match only with the same Memory SSA version, as in
[O0185](O0185-cse-past-merge.md).
