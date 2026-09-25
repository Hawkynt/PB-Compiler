# O0018 — Interprocedural constant propagation

| | |
|---|---|
| **Status** | ✅ Implemented (scalar parameters) |
| **Stage** | IR middle end (interprocedural module pass) |
| **Source** | `Ir/Passes/IpConstantProp.cs` — `PropagateArguments`; visibility from `Ir/Analysis/IrCallGraph.cs` — `IsFullyVisible` |
| **Gate** | `--optimize`; declined per procedure for `main`, a procedure with an error handler, and any procedure whose address is used other than as a direct callee |
| **Verified by** | `tests/diff/DIFF38.BAS`, `IpConstantPropTests`, `IrPassObservableEquivalenceTests` |
| **Related** | [O0017](O0017-sccp.md), [O0025](O0025-pure-function-folding.md), [O0069](O0069-dead-parameter-elimination.md) |

## What it is

A scalar parameter that receives the **same compile-time constant at every call
site** reads as that literal inside the callee. That feeds constant
folding, dead-code elimination and branch folding *inside the procedure body*,
which is where the payoff is.

`PropagateArguments` replaces every use of such a parameter with the constant;
the pass runs in the interprocedural phase of the standard pipeline, so the
function pipeline that follows folds what it exposed. The same pass also
propagates a constant *result* the other way (O0159). It leaves the signature
alone itself; the parameter it leaves unused is handed to
[O0069](O0069-dead-parameter-elimination.md) at the end of the pass.

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

- Every call site must be visible and must pass the same constant, of the
  parameter's own type. What is replaced is the value the parameter *arrives*
  with; a later write to the parameter inside the body defines a new value and
  is unaffected.
- `IsFullyVisible` declines `main` and any procedure whose address is used
  anywhere but as the callee of a direct call (`CODEPTR` / `CALL DWORD`, a
  delegate, an argument), because an indirect call could pass an argument the
  analysis never saw. The decision is per procedure.
- Replacing the parameter's uses does not change the ABI, so no caller has to
  agree with the callee about the specialization.

## Limits

Dropping the now-dead parameter, and cloning a procedure for one dominant
argument shape, are [O0069](O0069-dead-parameter-elimination.md); passing
arguments in registers is [O0021](O0021-register-parameters.md).
