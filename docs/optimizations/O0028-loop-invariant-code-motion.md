# O0028 — Loop-invariant code motion

| | |
|---|---|
| **Status** | ✅ Implemented (any natural loop with a unique entering block, including bodies with `IF`/`SELECT` blocks) |
| **Stage** | IR middle end (loop optimization) |
| **Source** | `Ir/Passes/Licm.cs` — `Hoist`, `IsSpeculatable`, `IsHoistableRead` (in `IrMiddleEndPipeline.Standard`; loops from `Ir/Analysis/IrLoopAnalysis.cs`) |
| **Gate** | `--optimize`; only pure, trap-free instructions move |
| **Verified by** | `tests/diff/DIFF66.BAS`, `DoLoopLicmTests`, `PortedMidEndOptimizationsTests` (over a runtime-bounded loop; a constant-trip one is unrolled away first) |
| **Related** | [O0003](O0003-common-subexpression-elimination.md), [O0049](O0049-ir-licm.md), [O0060](O0060-memory-ssa.md) |

## What it is

A pure, trap-free instruction inside a loop whose operands are all defined
outside it (directly or through other hoisted instructions) is moved to the
loop's unique entering block, so it runs once; every iteration uses the same
SSA value, and the register allocator decides whether it stays in a register or
a spill slot.

Loops are the natural loops of the control-flow graph, so `FOR`, `DO`/`WHILE`
and `GOTO` loops are treated alike: the counter, or anything else written in
the loop, is a value defined inside it and is not invariant. Innermost loops go
first, so a value can climb out of a nest over repeated runs.

Bodies containing `IF`/`SELECT` blocks are supported: an instruction in a
conditional block is hoisted too, which is safe because it is pure and cannot
trap. A deterministic read-only runtime call — `LEN` of a string — is also
hoisted, but only when nothing in the loop writes memory, allocates, frees or
can raise.

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

**Zero-trip safety** is the whole problem: the entering block runs even when the
body does not, so a hoisted instruction must not be able to trap where the
original never ran. Hence only instructions the effect model marks as
speculatable move — arithmetic, compares, casts, address computations and calls
without side effects or traps — while loads, stores, allocas and anything that
can trap (every integer or float division) stay put. Under `$ERROR` checking
the check is a compare and a branch to the raise; the branch is never moved, so
the raise still happens in the iteration that fails.

## Limits

Hoisting **loads** (array elements, string handles) needs alias / memory SSA —
[O0060](O0060-memory-ssa.md). Sinking rarely-used computations into their branch
is roadmap.
