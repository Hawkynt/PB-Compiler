# O0031 — Branch fusion

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.cs` (the armed comparison node), `CodeGen/CodeGenerator.Expressions.cs` |
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

The fusion is **armed for one node and matched by identity**, so a comparison
nested inside a larger expression, or emitted from an inlined callee body, is
never mistaken for the condition itself. The arming site falls back to the value
path whenever the fusion did not fire — for example when the comparison was
folded ([O0016](O0016-value-fact-analysis.md)) or strength-reduced first — so
the two paths can never both apply or both be skipped.

The branch sense is chosen so the fall-through is the `THEN` body, which is also
what the 8086's static prediction prefers ([O0041](O0041-branch-layout.md)).
