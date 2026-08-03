# O0236 — 32-bit shift/rotate collapse

| | |
|---|---|
| **Status** | ✅ Implemented (constant counts 1..31) |
| **Stage** | Emitter |
| **Source** | `CodeGen` — `EmitShiftRotate` |
| **Gate** | `--optimize` + `$CPU 80386` |
| **Verified by** | `tests/diff/DIFF70.BAS` (byte-identical to genuine PBC 3.50, which accepts `$CPU 80386`) |
| **Split from** | [C0001](C0001-386-codegen.md) |

## What it is

A `LONG` `SHIFT`/`ROTATE` statement with a constant count of 1..31 becomes a
single `SHL`/`SHR`/`ROL`/`ROR dword [cell], imm8`, instead of the CX-times
per-word `RCL`/`RCR` loop the 16-bit path needs.

## Sample

```basic
$CPU 80386
DIM v&
ROTATE LEFT v&, 3
```

## Without / with

```asm
    mov     cx, 0003h        ; without: a loop over the word pair
Top:
    rcl     word ptr [v], 1
    rcl     word ptr [v+2], 1
    loop    Top

    rol     dword ptr [v], 3 ; with
```

## Why it is safe

Constant 1..31 only, for the 5-bit count-masking reason
([O0235](O0235-shld-shrd-shifts.md)). `SHIFT RIGHT` stays **logical**, matching
the statement's defined semantics rather than the arithmetic shift an
expression-level `\` would use.
