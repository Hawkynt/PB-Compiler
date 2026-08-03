# O0184 — CSE inheritance into dominated branches

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Pre-emission analysis + emitter |
| **Source** | `CodeGen/OptCommonSubexpr.cs` |
| **Gate** | `--optimize`; barrier-free condition |
| **Split from** | [O0003](O0003-common-subexpression-elimination.md) |

## What it is

The live value cache from before an `IF`/`SELECT` is **inherited** into the
arms, which the condition dominates. A value computed before the branch and
recomputed inside it reloads the slot instead.

The `y*320+x` "compute, then reuse inside a branch" pattern is the corpus's most
common shape for this.

## Sample

```basic
DIM x%, y%, o%
o% = y% * 320 + x%
IF flag% THEN o% = y% * 320 + x% + 1
```

## With the optimizer

```asm
    ...                      ; define the CSE slot before the IF
    mov     [bp-6], ax
    ...
    mov     ax, [bp-6]       ; inside the arm: reload, not recompute
    inc     ax
```

## Why it is safe

The condition must be barrier-free, so nothing between the define and the arm
can write the inputs. A reload only ever follows a define from identical inputs,
so any `$ERROR` trap fires exactly where the un-CSE'd occurrence would have.
