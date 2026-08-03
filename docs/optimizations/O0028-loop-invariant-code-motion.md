# O0028 — Loop-invariant code motion

| | |
|---|---|
| **Status** | ✅ Implemented (FOR and DO/WHILE loops, including bodies with `IF`/`SELECT` blocks) |
| **Stage** | Pre-emission analysis + emitter preheader |
| **Source** | `CodeGen/OptCommonSubexpr.cs` — `AnalyzeLicm`; `CodeGen/CodeGenerator.cs` — `EmitLicmPreheader` |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED`, no checked arithmetic, no error handler |
| **Verified by** | `tests/diff/DIFF66.BAS`, `DoLoopLicmTests` |
| **Related** | [O0003](O0003-common-subexpression-elimination.md), [O0049](O0049-ir-licm.md), [O0060](O0060-memory-ssa.md) |

## What it is

A pure integer (or modular-int16) subexpression inside a loop body whose
operands are all **loop-invariant** is computed once in the loop's preheader
into a CSE frame slot, and each iteration reloads the slot.

For a `FOR` loop, "invariant" excludes the counter. A `DO`/`WHILE` loop has no
counter, so invariance is simply "not written in the body" (the analysis takes a
nullable counter).

Bodies containing `IF`/`SELECT` blocks are supported: the write-set scan unions
every **conditional** write, unconditionally evaluated expressions (flat
statements, the `IF`'s first condition, a `SELECT`'s subject) still hoist, and
branch-only expressions stay where they are.

## Sample

```basic
$OPTIMIZE SPEED
DIM i%, w%, h%, a%(0 TO 999)
w% = 40 : h% = 25
FOR i% = 0 TO 999
  a%(i%) = w% * h% + i%
NEXT
```

## Without the optimizer

`w% * h%` is recomputed on all 1 000 iterations:

```asm
Top:
    ...
    mov     ax, [w]
    mov     bx, [h]
    imul    bx
    add     ax, [i]
    ...
    jmp     Top
```

## With the optimizer

```asm
    mov     ax, [w]          ; preheader: once
    mov     bx, [h]
    imul    bx
    mov     [bp-8], ax
Top:
    ...
    mov     ax, [bp-8]       ; reload
    add     ax, [i]
    ...
    jmp     Top
```

## Equivalent BASIC

```basic
DIM i%, w%, h%, t%, a%(0 TO 999)
w% = 40 : h% = 25
t% = w% * h%
FOR i% = 0 TO 999 : a%(i%) = t% + i% : NEXT
```

## Why it is safe

**Zero-trip safety** is the whole problem: the preheader runs even when the body
does not, so a hoisted expression must not be able to trap where the original
never ran. Hence the gates — `$OPTIMIZE SPEED` only, no checked arithmetic, no
error handler in scope, and `\`/`MOD` need a constant non-zero divisor. Under
those conditions the hoisted expression is pure and total.

## Limits

Hoisting **loads** (array elements, string handles) needs alias / memory SSA —
[O0060](O0060-memory-ssa.md). Sinking rarely-used computations into their branch
is roadmap.
