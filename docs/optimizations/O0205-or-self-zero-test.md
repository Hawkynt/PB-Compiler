# O0205 — Zero test as `OR reg,reg`

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Expressions.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF44.BAS` |
| **Split from** | [O0008](O0008-peephole-zero-idiom.md) |

## What it is

A comparison against zero collapses to `OR AX,AX` — two bytes instead of three,
with the same ZF and SF.

## Sample

```basic
DIM n%
IF n% = 0 THEN PRINT "zero"
```

## Without / with

```asm
    mov     ax, [n]
    cmp     ax, 0000h        ; 3 bytes
    ; becomes
    mov     ax, [n]
    or      ax, ax           ; 2 bytes
```

## Why it is safe

`OR` clears OF, which `CMP` would have set — and that is harmless here: with
OF = 0 both the signed and the unsigned conditions reduce to SF/CF tests, so
every `Jcc` that can follow a zero comparison reads the same answer.

## See also

Reusing flags an earlier ALU instruction already set (so that no compare is
emitted at all) is [O0081](O0081-flag-reuse.md).
