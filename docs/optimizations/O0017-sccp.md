# O0017 — SCCP and branch folding (the SSA mid-end)

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | IR middle end (scalar simplification) |
| **Source** | `Ir/Passes/Sccp.cs` — `Solver.Solve`, `Rewrite`; constants evaluated by `Ir/IrConstFold.cs`; SSA from `Ir/Passes/Mem2Reg.cs` |
| **Gate** | `--optimize`; under `$ERROR` checking the trap tests are ordinary IR compares, folded like any other |
| **Verified by** | `tests/diff/DIFF48.BAS`, `DIFF49.BAS` (loops), `PowerBasic.Compiler.Tests/Ir/SccpTests.cs` |
| **Related** | [O0001](O0001-constant-folding.md), [O0002](O0002-dead-code-elimination.md), [O0016](O0016-value-fact-analysis.md), [O0044](O0044-ir-sccp.md) |
| **Split into** | [O0225](O0225-ssa-construction.md), [O0226](O0226-proven-constant-reads.md) |

## What it is

**This page covers the SCCP solve.** `Sccp` solves the constant lattice **and**
block reachability together (Wegman-Zadeck): a branch whose condition folds
constant lights only its taken edge, so phi merges ignore dead arms, and PB's
zero-initialized locals make an uninitialized read provably zero.

That is strictly more powerful than local folding, because it sees through phis
and dead control flow.

The SSA form it runs on is [O0225](O0225-ssa-construction.md). After the solve
`Sccp` rewrites what it proved itself: constant values are replaced by their
constants, constant conditional branches become unconditional, and blocks that
became unreachable are deleted; see also
[O0226](O0226-proven-constant-reads.md).

## Sample

```basic
DIM mode%, scale%, out%
mode% = 2
IF mode% = 1 THEN
  scale% = 10
ELSE
  scale% = 4
END IF
out% = scale% * 3
PRINT out%
```

## Without the optimizer

Every assignment is stored, the branch is really taken, and the multiply runs:

```asm
    mov     ax, 0002h
    mov     [mode], ax
    mov     ax, [mode]
    cmp     ax, 0001h
    jne     Else
    mov     ax, 000Ah
    mov     [scale], ax
    jmp     EndIf
Else:
    mov     ax, 0004h
    mov     [scale], ax
EndIf:
    mov     ax, [scale]
    mov     bx, 0003h
    imul    bx
    mov     [out], ax
```

## With the optimizer

SCCP proves `mode% = 2`, so the `THEN` arm is unreachable and never emitted;
`scale%` is 4 at the merge, `out%` is 12, and the stores die
([O0002](O0002-dead-code-elimination.md)):

```asm
    mov     ax, 000Ch
    call    rt_print_i16
```

## Equivalent BASIC

```basic
PRINT 12
```

## Why it is safe

- It runs on the IR's control-flow graph, which the lowering builds for every
  construct, `GOTO`/`GOSUB` included. `Mem2Reg` promotes only stack slots whose
  every use is a direct load or store of the slot's own shape; a variable whose
  address escapes stays in memory, and loads, calls and other opaque values sit
  at the lattice's bottom.
- The cyclic constant lattice converges because the lattice is monotone.
- The arithmetic is `IrConstFold`'s: integer results wrap to the result type's
  width, and an operation whose result is undefined (division by zero,
  `INT_MIN / -1`, out-of-range shifts or float-to-integer conversions) is not
  folded, so its runtime behaviour — trap included — is kept.
- `$ERROR OVERFLOW/NUMERIC/BOUNDS` checks are lowered as an explicit compare and
  a branch to the raise, so folding the arithmetic folds the check with it: a
  proven overflow still raises, and a check proven unable to fire goes away.
