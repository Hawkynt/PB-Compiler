# O0220 — Divide-by-zero guard elimination

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter, on the value lattice |
| **Source** | `CodeGen/CodeGenerator.cs` — `DivisorNonZero` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF80.BAS` |
| **Split from** | [O0016](O0016-value-fact-analysis.md) |

## What it is

The Error-11 guard before an `INTEGER` `\` or `MOD` is emitted
**unconditionally** — it is not an `$ERROR` option but part of the language's
behavior. When the divisor's proven range excludes zero, the guard cannot fire
and is dropped.

## Sample

```basic
DIM i%, q%
FOR i% = 1 TO 100            ' the counter range excludes 0
  q% = 1000 \ i%
NEXT
```

## Without / with

```asm
    mov     bx, si           ; without
    or      bx, bx
    jz      rt_err_div
    cwd
    idiv    bx

    mov     bx, si           ; with
    cwd
    idiv    bx
```

## Why it is safe

The divisor's range must **exclude zero on every path** that reaches the divide.
The guard is behavior, not diagnostics: dropping it where zero is possible would
turn a clean Error 11 into a CPU divide fault, so the proof is required to be
exact rather than probable.
