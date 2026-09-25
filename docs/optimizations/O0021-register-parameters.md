# O0021 — Register parameters

| | |
|---|---|
| **Status** | ✅ Implemented (procedures whose parameters are all one word, leading four in AX/DX/BX/CX) |
| **Stage** | IR module pass (after the call graph is final) + x86 back end (both sides of the call) |
| **Source** | `Ir/Passes/PrivateCallingConvention.cs` (run last in `IrMiddleEndPipeline.RunNativeModule`); `Backend/X86CallAbi.cs` (the `WATCALL` register list); `Backend/MachineEmitter.cs` — `EmitFunction` |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED`, `pb36` only |
| **Related** | [O0006](O0006-inlining.md), [O0018](O0018-interprocedural-constant-propagation.md), [O0282](O0282-internal-calling-convention.md), [docs/LINKER.md](../LINKER.md) (`WATCALL`) |

## What it is

When the compiler owns **every** call site of a procedure — it is defined in a
self-contained module and its address is never taken via `CODEPTR`/`CALL DWORD`
— and every parameter is one word, its leading parameters travel in registers
(AX, DX, BX, CX; any further ones stay on the stack) instead of being pushed,
reusing the existing `WATCALL` convention. `PrivateCallingConvention` switches
the definition and every call site to `WATCALL` together in the IR, and the back
end reads both from there, so the behavior is identical and the per-call
push/pop traffic disappears.

This is the same pass as [O0282](O0282-internal-calling-convention.md), which
owns the per-procedure policy: an address escape fences the escaped target
instead of disabling unrelated procedures, and one-word BYREF near pointers use
the same register slots as word-sized `BYVAL` integers.

## Sample

```basic
$OPTIMIZE SPEED
FUNCTION Add3%(BYVAL a%, BYVAL b%, BYVAL c%)
  Add3% = a% + b% + c%
END FUNCTION

PRINT Add3%(1, 2, 3)
```

## Without the optimizer

```asm
    mov     ax, 0001h
    push    ax
    mov     ax, 0002h
    push    ax
    mov     ax, 0003h
    push    ax
    call    Add3
    ...
Add3:
    push    bp
    mov     bp, sp
    mov     ax, [bp+8]       ; a%
    add     ax, [bp+6]       ; b%
    add     ax, [bp+4]       ; c%
    ...
    ret     6
```

## With the optimizer

```asm
    mov     ax, 0001h
    mov     dx, 0002h
    mov     bx, 0003h
    call    Add3
    ...
Add3:
    add     ax, dx
    add     ax, bx
    ret                       ; nothing to clean
```

## Equivalent BASIC

Unchanged — this is a calling-convention choice, not a source transformation.
The `pb36` spelling of the same thing by hand would be a `WATCALL` declaration.

## Why it is safe

- An explicitly address-taken procedure keeps its BASIC convention; unrelated
  procedures may still specialize under O0282's per-procedure ownership proof.
- Typed procedure-pointer dispatch remains a conservative module-wide fence until
  its complete target set is represented and can be proven.
- It is **skipped when external units or libraries are linked**, or when the
  module is compiled as a unit (`IrModule.OwnsProcedureAbi`) — such callers use
  the stack convention and were compiled without this knowledge. Inline assembly
  anywhere in the program disables it too, since a text `CALL` is a caller the
  IR cannot see.
- It is not applied to non-`pb36` dialects at all, so the golden output of every
  historic dialect is untouched.
- Caller and callee always flip together, per procedure, in the same compilation.

## Limits

LONG, float and pointer arguments in register *pairs* are the remaining piece
(see [O0282](O0282-internal-calling-convention.md)); the general internal calling
convention also composes with BYREF collapse and dead-parameter elimination
([O0069](O0069-dead-parameter-elimination.md)) and segment-register allocation
([O0071](O0071-segment-register-allocation.md)).
