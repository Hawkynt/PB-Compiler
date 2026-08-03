# O0029 — `SELECT CASE` → jump table

| | |
|---|---|
| **Status** | ✅ Implemented (dense 16-bit single-constant cases) |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.cs` — `TryEmitSelectJumpTable` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF62.BAS` (under `pb35` and `pb36`) |
| **Related** | [O0067](O0067-if-chain-jump-table.md) |

## What it is

A 16-bit `SELECT CASE` whose arms are all single integer constants (no ranges,
no `IS` relations) with a **dense** value span dispatches through a word table
instead of a compare chain: subtract the minimum, do one *unsigned* bounds
check, then `JMP [table + index*2]`.

Density thresholds: at least 4 cases, span ≤ 256 and span ≤ 4 × case count —
otherwise the table would cost more bytes than the compares it saves.

## Sample

```basic
SELECT CASE k%
  CASE 10 : PRINT "a"
  CASE 11 : PRINT "b"
  CASE 12 : PRINT "c"
  CASE 13 : PRINT "d"
  CASE ELSE : PRINT "?"
END SELECT
```

## Without the optimizer

Up to four compares before the last arm runs:

```asm
    mov     ax, [k]
    cmp     ax, 000Ah
    je      Arm1
    cmp     ax, 000Bh
    je      Arm2
    cmp     ax, 000Ch
    je      Arm3
    cmp     ax, 000Dh
    je      Arm4
    jmp     Default
```

## With the optimizer

Constant time, whichever arm matches:

```asm
    mov     ax, [k]
    sub     ax, 000Ah        ; normalize to 0-based
    cmp     ax, 0003h
    ja      Default          ; unsigned: catches negatives too
    shl     ax, 1
    mov     bx, ax
    jmp     word ptr [Table+bx]
Table:
    dw      Arm1, Arm2, Arm3, Arm4
```

## Equivalent BASIC

```basic
ON k% - 9 GOTO Arm1, Arm2, Arm3, Arm4     ' with a range guard around it
```

## Why it is safe

The arms are emitted unchanged and the same arm runs for every input, so the
output is byte-identical. The single **unsigned** compare after the subtraction
handles both ends at once — a subject below the minimum wraps to a large
unsigned value and lands in the default. `CASE ELSE` (or the absence of a match)
targets the default label exactly as the compare chain did.

## Limits

Ranges (`CASE 1 TO 9`), `CASE IS` relations, string subjects and sparse value
sets keep the compare chain. A chain of mutually exclusive `IF x = k` tests is
not yet recognized as the same shape — that is
[O0067](O0067-if-chain-jump-table.md).
