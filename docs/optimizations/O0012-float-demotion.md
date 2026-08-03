# O0012 — Float demotion ("de-floating")

| | |
|---|---|
| **Status** | ✅ Implemented (FOR counters and integral constant resets) |
| **Stage** | Pre-emission analysis (whole body) |
| **Source** | `CodeGen/OptFloatDemotion.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF28.BAS` (demoted and blocked twins side by side) |
| **Related** | [O0013](O0013-promotion-lowering.md), [O0037](O0037-fixed-point-for-counters.md), [O0057](O0057-storage-narrowing.md) |

## What it is

PB defaults a bare variable name to **SINGLE**, so most DOS-era loop counters
and flags are floating-point by accident, not by intent. When the analysis
proves a SINGLE/DOUBLE variable only ever holds integral values inside
INTEGER/LONG range, and every read sits in a value-exact context, the variable
is silently re-typed to INTEGER or LONG — and the whole x87 round trip
disappears.

## Sample

```basic
DIM i                 ' SINGLE by PB's default typing — a float by accident
DIM total AS SINGLE   ' SINGLE on purpose — demoted just the same
total = 0
FOR i = 1 TO 10
  total = total + i
NEXT
PRINT total
```

The declaration is not a promise about representation, only about observable
values: an explicit `AS SINGLE` is re-typed exactly like the implicit one, as
long as every value and every use is provably integral.

## Without the optimizer

Every iteration runs through the FPU and a memory staging cell:

```asm
Top:
    fld     dword ptr [i]
    fld     dword ptr [limit]
    fcompp
    fstsw   ax
    sahf
    ja      Done
    fld     dword ptr [total]
    fadd    dword ptr [i]
    fstp    dword ptr [total]
    fld     dword ptr [i]
    fadd    dword ptr [one]
    fstp    dword ptr [i]
    jmp     Top
Done:
```

## With the optimizer

`i` and `total` become 2-byte integer cells (and are then eligible for register
residency, [O0005](O0005-register-residency.md)):

```asm
    mov     si, 0001h
    xor     di, di
Top:
    cmp     si, 000Ah
    jg      Done
    add     di, si
    inc     si
    jmp     Top
Done:
    mov     [i], si
    mov     [total], di
```

## Equivalent BASIC

The re-typing *is* the transformation, so the equivalent source is the same
program with different declarations — including for the explicitly declared one:

```basic
DIM i AS INTEGER          ' was: DIM i            (implicit SINGLE)
DIM total AS INTEGER      ' was: DIM total AS SINGLE
total = 0
FOR i = 1 TO 10 : total = total + i : NEXT
PRINT total
```

PRINT formatting is safe by construction: an integral float already prints
without a decimal point in genuine PBC (see `docs/QUIRKS.md`), so the demoted
program's output is identical.

## Why it is safe

The demotion is blocked or killed by anything that could observe the float
representation:

- a `/` or `^` operator, a fractional literal, or an intrinsic over the value;
- `PRINT USING` or `WRITE #` of the variable;
- a call argument (BYREF), `INPUT`/`READ`/`SWAP` target, or `INCR` (unbounded);
- a non-integral `CASE` comparison;
- inline asm or indirect control flow anywhere in the body.

Range proofs respect SINGLE's 2²⁴ exact-integer bound, so a value that a SINGLE
could not have represented exactly is never demoted.

## Limits

Only FOR-header writes plus integral constant resets are proven today. General
whole-program value tracking — a SINGLE assigned from arbitrary integral
expressions — waits on the value-fact lattice
([O0016](O0016-value-fact-analysis.md)) and storage narrowing
([O0057](O0057-storage-narrowing.md)).
