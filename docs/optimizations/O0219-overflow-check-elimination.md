# O0219 — Overflow-check elimination

| | |
|---|---|
| **Status** | ✅ Implemented (16- and 32-bit add/subtract) |
| **Stage** | Emitter, on the value lattice |
| **Source** | `CodeGen/CodeGenerator.cs` — `ProvablyNoOverflow`, `ProvablyNoOverflow32` |
| **Gate** | `--optimize` + `$ERROR OVERFLOW ON` |
| **Verified by** | `tests/diff/DIFF79.BAS` (16-bit), `DIFF87.BAS` (32-bit) |
| **Split from** | [O0016](O0016-value-fact-analysis.md) |

## What it is

An `INTEGER` add or subtract over an affine counter range that provably stays
inside 16 bits cannot raise Error 6, so its `JNO` guard is not emitted. The same
proof extends to `LONG` add/subtract whose exact value range stays inside the
signed 32-bit range, dropping the guard after the `ADD`/`ADC` (`SUB`/`SBB`)
pair.

## Sample

```basic
$ERROR OVERFLOW ON
DIM i%, t%
FOR i% = 0 TO 99
  t% = i% + 1                ' [1,100]: cannot overflow
NEXT
```

## Without / with

```asm
    mov     ax, si           ; without
    add     ax, 0001h
    jno     Ok
    call    rt_err_ovf
Ok:

    mov     ax, si           ; with
    add     ax, 0001h
```

## Why it is safe

The proof must cover the **exact** value range of the operation, computed from
ranges that are themselves type-validated — the guard is dropped only where
Error 6 provably cannot fire, never merely where it is unlikely. Multiplication
is excluded: the promoted path handles it, and its overflow behavior is a
separate question ([O0224](O0224-bounded-multiply-off-fpu.md)).
