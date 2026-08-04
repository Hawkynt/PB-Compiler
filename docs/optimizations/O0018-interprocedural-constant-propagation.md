# O0018 — Interprocedural constant propagation

| | |
|---|---|
| **Status** | ✅ Implemented (scalar parameters, unchanged ABI) |
| **Stage** | Whole-program pre-emission analysis |
| **Source** | `CodeGen/OptIpcp.cs` |
| **Gate** | `--optimize`; disabled wholesale when any procedure address is taken |
| **Verified by** | `tests/diff/DIFF38.BAS` |
| **IR** | ✅ `Ir/Passes/IpConstantProp.cs` — `PropagateArguments`: when every visible call passes the same constant for a parameter, the parameter becomes that constant. Registered with `IrPassManager.AddModulePass`, so `RunOnModule` runs the function pipeline, then this, then the function pipeline again for what it exposed. Soundness rests on `IsFullyVisible`, which declines `main` and any function whose address appears anywhere but a callee operand; verified by `IpConstantPropTests` and `IrPassObservableEquivalenceTests` |
| **Related** | [O0017](O0017-sccp.md), [O0025](O0025-pure-function-folding.md), [O0069](O0069-dead-parameter-elimination.md) |

## What it is

A scalar parameter that receives the **same compile-time constant at every call
site** and is never written — neither directly nor by being passed BYREF to
another procedure — reads as that literal inside the callee. That feeds constant
folding, dead-code elimination and branch folding *inside the procedure body*,
which is where the payoff is.

The calling convention is untouched: the argument is still pushed and the frame
slot still exists. Only the body specializes, so the call sites are
byte-identical.

## Sample

```basic
SUB Draw(BYVAL mode%, BYVAL x%)
  IF mode% = 1 THEN
    PRINT "text"; x%
  ELSE
    PRINT "gfx"; x%
  END IF
END SUB

CALL Draw(1, 10)
CALL Draw(1, 20)
```

## Without the optimizer

The body tests `mode%` on every invocation and both arms are emitted:

```asm
Draw:
    push    bp
    mov     bp, sp
    mov     ax, [bp+8]       ; mode%
    cmp     ax, 0001h
    jne     Gfx
    ...                      ; "text" arm
    jmp     Done
Gfx:
    ...                      ; "gfx" arm
Done:
    ...
```

## With the optimizer

`mode%` is 1 at every call site and never written, so the read is the literal 1,
the condition folds, and the `ELSE` arm is unreachable:

```asm
Draw:
    push    bp
    mov     bp, sp
    ...                      ; only the "text" arm
    mov     sp, bp
    pop     bp
    ret     4
```

The `"gfx"` literal also leaves the string pool.

## Equivalent BASIC

```basic
SUB Draw(BYVAL mode%, BYVAL x%)     ' signature unchanged
  PRINT "text"; x%
END SUB
```

## Why it is safe

- Every call site must be visible and must pass the same constant; a parameter
  written anywhere in the callee (in a statement or an expression, directly or
  through a BYREF hand-off) disqualifies it.
- The pass is disabled wholesale when any procedure's address is taken
  (`CODEPTR` / `CALL DWORD`), because an indirect call could pass an argument
  the analysis never saw.
- Because the ABI is unchanged, no caller has to agree with the callee about the
  specialization.

## Limits

Dropping the now-dead parameter, and cloning a procedure for one dominant
argument shape, are [O0069](O0069-dead-parameter-elimination.md); passing
arguments in registers is [O0021](O0021-register-parameters.md).
