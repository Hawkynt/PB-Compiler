# O0012 — Float demotion ("de-floating")

| | |
|---|---|
| **Status** | 🟡 Partial — FOR counters with integral constant start, step and limit; integral constant resets and accumulators are not demoted on the IR path |
| **Stage** | IR middle end |
| **Source** | `Ir/Passes/FloatDemotion.cs` — `Run`, `Demote`, `IsRewritableUse`, `Rewrite`; runs after `Mem2Reg` has made the counter a phi |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF28.BAS` (demoted and blocked twins side by side), `FloatDemotionTests`, `IrPassObservableEquivalenceTests` |
| **Related** | [O0013](O0013-promotion-lowering.md), [O0037](O0037-fixed-point-for-counters.md), [O0057](O0057-storage-narrowing.md) |

## What it is

PB defaults a bare variable name to **SINGLE**, so most DOS-era loop counters
and flags are floating-point by accident, not by intent. After `Mem2Reg` a
float `FOR` counter is a phi. `FloatDemotion` accepts a float phi whose entry
value is an integral constant, whose value round the latch is itself plus or
minus an integral constant, and which is compared against an integral
constant; all three must lie within ±32767. Such a counter is replaced by an
`i32` phi, and the x87 round trip disappears. The lowering writes a `FOR`
bound as `sitofp i16 10 to f32` rather than as a float literal, so `Integral`
accepts both spellings.

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
values: an explicitly declared SINGLE counter is demoted exactly like an
implicit one. On the IR path only the counter shape qualifies, and only when
every use of it can be rewritten: in this sample `i` feeds `total + i`, whose
other operand is not a constant, so the counter is not demoted, and `total` is
not a counter at all. The listing below shows the shape once both are
integral.

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

Demotion is only sound where the counter is bounded, because integer
arithmetic wraps where float arithmetic saturates. With start, step and limit
all inside 16 bits, and the step required to move toward the limit
(`Bounded`), the `i32` counter cannot leave its range whatever the trip count.
The increment may feed nothing but the phi, and every other use must be one of:

- a conversion back to an integer, which becomes the identity (or a
  truncation) — the whole saving;
- `+`, `-` or `*` against an integral constant, rewritten as integer
  arithmetic followed by a conversion back to float for its users;
- a comparison against an integral constant.

Anything else — a call, a store, a return, arithmetic with a non-constant
value — declines the whole counter. A function with an armed error handler or
inline assembly is never touched. Values within ±32767 are exact in SINGLE, so
the demoted counter holds the same values the float one did.

## Limits

Only the counter phi shape is demoted. Integral constant resets, accumulators
and a SINGLE assigned from arbitrary integral expressions are not; the
syntax-level analysis that covered resets was retired with the direct emitter.
General value tracking belongs to the value-fact analyses
([O0016](O0016-value-fact-analysis.md)) and storage narrowing
([O0057](O0057-storage-narrowing.md)).
