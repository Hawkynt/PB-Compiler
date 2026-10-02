# O0226 — Cross-block proven-constant reads

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR middle end |
| **Source** | `Ir/Passes/Sccp.cs` (after `Ir/Passes/Mem2Reg.cs` has turned the variable's slot into SSA values) |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF48.BAS`, `DIFF49.BAS` |
| **Split from** | [O0017](O0017-sccp.md) |

## What it is

After `Mem2Reg`, a read of a variable is an SSA value, possibly a phi. SCCP
([O0017](O0017-sccp.md)) replaces every value it proves constant with that
constant — constant propagation **across blocks** and through phis, which a
folder that sees one expression at a time ([O0001](O0001-constant-folding.md))
cannot do.

## Sample

```basic
DIM k%, r%
k% = 7
IF flag% THEN PRINT "x"
r% = k% * 2                  ' k% is 7 here, across the branch
```

## With the optimizer

```asm
    mov     ax, 000Eh        ; the read folded, then the multiply folded
    mov     [r], ax
```

## Why it is safe

Every stored value is wrapped to its variable's type, so a proven constant is
the exact value the program computes. The `$ERROR` traps do not force the fold
off on this path: the IR lowering spells each trap as an explicit compare and a
branch to `rt_error`, so folding the arithmetic folds the trap condition with it
and a trap that must fire still reaches its raise. A function with an armed
`ON ERROR` handler is skipped by the function pass pipeline entirely.
