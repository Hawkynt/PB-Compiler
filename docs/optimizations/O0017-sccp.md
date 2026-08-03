# O0017 — SCCP and branch folding (the SSA mid-end)

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | SSA mid-end, between binder and emitter |
| **Source** | `CodeGen/Ssa/ControlFlowGraph.cs`, `DominatorTree.cs`, `SsaForm.cs`, `Sccp.cs` |
| **Gate** | `--optimize`; proven-constant reads are **not** folded under `$ERROR OVERFLOW/NUMERIC` |
| **Verified by** | `tests/diff/DIFF48.BAS`, `DIFF49.BAS` (loops), `PowerBasic.Compiler.Tests/CodeGen/SsaTests.cs` |
| **Related** | [O0001](O0001-constant-folding.md), [O0002](O0002-dead-code-elimination.md), [O0016](O0016-value-fact-analysis.md), [O0044](O0044-ir-sccp.md) |
| **Split into** | [O0225](O0225-ssa-construction.md), [O0226](O0226-proven-constant-reads.md) |

## What it is

**This page covers the SCCP solve.** `Sccp` solves the constant lattice **and**
block reachability together (Wegman-Zadeck): a branch whose condition folds
constant lights only its taken edge, so phi merges ignore dead arms, and PB's
zero-initialized locals make an uninitialized read provably zero.

That is strictly more powerful than local folding, because it sees through phis
and dead control flow.

The SSA form it runs on is [O0225](O0225-ssa-construction.md); folding the
proven reads at the emitter is [O0226](O0226-proven-constant-reads.md).

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

- The graph is sound by construction: the builder **bails** on post-test loops,
  `GOTO`/labels, `GOSUB`, `ON ERROR` and anything else it cannot model
  precisely, so a body it does not understand is simply not optimized.
- Only non-escaping integral scalars are renamed; a variable that escapes via a
  BYREF call, an address intrinsic or any opaque statement is dropped from the
  analysis.
- The cyclic constant lattice converges because the lattice is monotone.
- The arithmetic is delegated to the emitter's `ConstantFolder` and every stored
  value is wrapped to its variable's type, so a proven constant is the exact
  value the program computes — including the wrap-guard from
  [O0001](O0001-constant-folding.md).
- Folding is disabled under `$ERROR OVERFLOW/NUMERIC`, where a folded constant
  would skip a runtime trap the real arithmetic must still raise.
