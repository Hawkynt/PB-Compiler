# O0031 — Branch fusion

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | x86 back end (instruction selection) |
| **Source** | `Backend/InstructionSelector.cs` — `FoldedCompare`, `EmitCompareForFlags`, `SelectTerminator`; `Backend/Peephole.cs` (branch layout) |
| **Gate** | `--optimize` |
| **Verified by** | scenario `ComparisonBranchesOnItsOwnFlags` |
| **Related** | [O0032](O0032-short-circuit-conditions.md), [O0008](O0008-peephole-zero-idiom.md) |

## What it is

PowerBASIC's truth value is −1/0, so a comparison normally *materializes* a
value: compare, load −1 or 0, and then the consumer tests it. But when the
comparison **is** the whole condition of an `IF`/`ELSEIF`/`WHILE`/`UNTIL` or a
ternary, nothing ever reads that value — the `CMP`'s own flags can drive the
branch.

Five instructions disappear from the shape almost every conditional has.

## Sample

```basic
DIM x%
IF x% > 10 THEN PRINT "big"
```

## Without the optimizer

```asm
    mov     ax, [x]
    cmp     ax, 000Ah
    jle     False
    mov     ax, 0FFFFh       ; materialize TRUE
    jmp     Have
False:
    mov     ax, 0000h        ; materialize FALSE
Have:
    test    ax, ax           ; and immediately consume it
    jz      EndIf
    ...                      ; PRINT "big"
EndIf:
```

## With the optimizer

```asm
    mov     ax, [x]
    cmp     ax, 000Ah
    jle     EndIf
    ...                      ; PRINT "big"
EndIf:
```

## Equivalent BASIC

Unchanged — only the intermediate truth value disappears.

## Why it is safe

`FoldedCompare` fuses a compare into the branch only when the block's
conditional branch is its **single user** and its predicate maps to a condition
code; a compare whose value is also used elsewhere, or a 32-bit compare, is
materialized and the branch tests that value against zero instead. A compare the
middle end has already folded to a constant never reaches selection
([O0017](O0017-sccp.md)). An IEEE float compare branches the same way, on the x87
status moved into the flags.

Selection emits `Jcc then` followed by `JMP else`; the peephole pass turns a
`Jcc next / JMP away` pair into a single inverted `Jcc away` when the `THEN`
body is laid out next, so the fall-through is the `THEN` body, which is also
what the 8086's static prediction prefers ([O0041](O0041-branch-layout.md)).
