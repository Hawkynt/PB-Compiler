# O0037 — Fixed-point FOR counters

| | |
|---|---|
| **Status** | ✅ Implemented (constant bounds/step on a power-of-two-fraction grid, 16-bit scaled counter) |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Optimize.cs` — `#region O13`, `TryEmitFixedPointFor` |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` |
| **Related** | [O0012](O0012-float-demotion.md), [O0075](O0075-silent-fixed-point.md) |

## What it is

A SINGLE/DOUBLE `FOR` counter whose constant bounds **and** step all sit on a
common power-of-two grid (`step = n × 2⁻ᵏ`) runs as a scaled 16-bit integer: the
loop compare and increment become plain `CMP`/`ADD`, and the float value is
materialized only where the body actually reads it (one `FILD` plus a multiply
by the exact power-of-two `2⁻ᵏ`).

The smallest `k ≤ 16` putting `from`, `to` and `step` exactly on the grid is
chosen; if no such `k` exists, or any scaled value would leave 16 bits, the pass
declines.

## Sample

```basic
$OPTIMIZE SPEED
DIM t
FOR t = 0 TO 1 STEP 0.25
  PRINT t
NEXT
```

## Without the optimizer

Every iteration pays a full x87 compare with the status-word round trip:

```asm
Top:
    fld     dword ptr [t]
    fld     dword ptr [limit]
    fcompp
    fstsw   ax
    sahf
    ja      Done
    ...                      ; body
    fld     dword ptr [t]
    fadd    dword ptr [step]
    fstp    dword ptr [t]
    jmp     Top
Done:
```

## With the optimizer

`k = 2`, so the counter runs 0, 1, 2, 3, 4 in a 16-bit cell:

```asm
    mov     word ptr [bp-2], 0000h
Top:
    mov     ax, [bp-2]
    cmp     ax, 0004h
    jg      Done
    fild    word ptr [bp-2]        ; materialize only where the body reads it
    fmul    dword ptr [quarter]
    fstp    dword ptr [t]
    ...                            ; body
    add     word ptr [bp-2], 0001h
    jmp     Top
Done:
```

## Equivalent BASIC

```basic
DIM i%, t
FOR i% = 0 TO 4
  t = i% / 4          ' exact: 4 is a power of two
  PRINT t
NEXT
```

## Why it is safe

Bit-exactness is provable, not assumed: every iterate `i × 2⁻ᵏ` is exactly
representable (|i| < 2¹⁵ here, far inside SINGLE's 2²⁴ exact-integer window) and
equals the genuine `FADD` chain `from + n × step`, while `FILD` followed by
`FMUL` by a power of two introduces no rounding. The counter cell ends on the
first failing value, matching the genuine `FOR`'s increment-then-test semantics.

## Limits

Only the loop counter is handled. Turning general float chains that carry a
provable constant scale into scaled-LONG arithmetic is
[O0075](O0075-silent-fixed-point.md).
