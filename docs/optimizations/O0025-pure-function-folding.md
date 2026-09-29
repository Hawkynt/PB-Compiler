# O0025 — Pure-function compile-time evaluation

| | |
|---|---|
| **Status** | ✅ Implemented (integer subset, v1) |
| **Stage** | IR module pass (interprocedural phase of `IrMiddleEndPipeline.Standard`) |
| **Source** | `Ir/Passes/PureCallEvaluation.cs` — `PureFunctions`, `Evaluate` |
| **Gate** | `--optimize` |
| **Sample** | [`docs/decompilation/optimizations/30-pure-function-folding.bas`](../decompilation/optimizations/30-pure-function-folding.bas) |
| **Related** | [O0001](O0001-constant-folding.md), [O0018](O0018-interprocedural-constant-propagation.md), [O0022](O0022-dead-procedure-elimination.md) |

## What it is

The compiler **infers** purity instead of requiring a `CONSTEXPR` keyword. A
`FUNCTION` whose IR does nothing but integer arithmetic, compares, casts, selects,
phis and branches — no memory, no runtime call, no error handler, no inline
assembly, no float — and calls only other pure functions is pure. Purity is a
greatest fixed point over the call graph, so recursive and mutually-recursive
pure helpers all qualify. Overflow checking needs no separate rule: where the
dialect checks, the lowering spells the check as a call to the runtime's error
raise, which disqualifies the function.

When such a function is called with **all-constant arguments**, the pass
interprets its SSA IR on the constants and the call is replaced by the result. The frame, the `CALL`/`RET` and the whole computation vanish;
once no caller references it, [O0022](O0022-dead-procedure-elimination.md)
purges the body.

## Sample

```basic
FUNCTION Fact&(BYVAL n%)
  LOCAL i%, r&
  r& = 1
  FOR i% = 2 TO n%
    r& = r& * i%
  NEXT
  Fact& = r&
END FUNCTION

PRINT Fact&(10)
```

## Without the optimizer

```asm
    mov     ax, 000Ah
    push    ax
    call    Fact             ; ten iterations at run time
    ...
Fact:
    push    bp
    mov     bp, sp
    ...                      ; loop, multiply, result slot, epilogue
    ret     2
```

## With the optimizer

```asm
    mov     si, s_0          ; " 3628800 " - the number is printed as text too
    mov     cx, 9
    call    rt_print_str
    call    rt_print_nl
```

…and `Fact` is not emitted at all.

## Equivalent BASIC

```basic
PRINT 3628800
```

## Why it is safe

- Every operation is evaluated by `IrConstFold`, the folder the rest of the
  middle end uses, at the operation's own width — so the folded value is
  **bit-identical** to what the compiled instruction computes; a 16-bit
  `INTEGER` product wraps in the interpreter too.
- Integer division or `MOD` by zero (which the folder refuses), an unmodelled
  instruction, or exhausting the step (200 000) / recursion (256) budget simply
  **abandons** the fold and keeps the genuine call.
- Folding a call to its provably-equal value cannot alter observable behavior,
  which is why the whole `pb36` differential battery is unchanged by this pass.

## Limits (v1 subset)

Integer-typed functions, parameters and locals only; any control flow the IR
expresses with branches and phis (`IF`, `SELECT CASE`, loops, `EXIT`). Floats,
strings, arrays, pointers and runtime intrinsics keep the real call — a roadmap
extension.
