# O0021 — Register parameters

| | |
|---|---|
| **Status** | ✅ Implemented (word values plus Watcom LONG pairs in AX/DX/BX/CX) |
| **Stage** | Whole-program analysis + emitter (both sides of the call) |
| **Source** | `CodeGen/OptRegParm.cs`, `CodeGen/CodeGenerator.Procs.cs` (`ConventionRegisters`) |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED`, `pb36` only |
| **Related** | [O0006](O0006-inlining.md), [O0018](O0018-interprocedural-constant-propagation.md), [O0282](O0282-internal-calling-convention.md), [docs/LINKER.md](../LINKER.md) (`WATCALL`) |

## What it is

When the compiler owns **every** call site of a procedure — it is defined in a
self-contained module and its address is never taken via `CODEPTR`/`CALL DWORD`
— its word-sized `BYVAL` scalar parameters travel in AX, DX, BX and CX instead
of on the stack. A LONG uses Watcom's DX:AX or CX:BX pair when one remains;
failure to find a legal pair sends that argument and every later one to the
stack. Caller and callee flip together, so behavior is unchanged while avoidable
call traffic disappears.

O0282 owns the per-procedure policy around this mechanism: an address escape
fences the escaped target instead of disabling unrelated procedures, and one-word
BYREF near pointers can use the same register slots. O0021 remains the underlying
argument placement mechanism.

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
- It is **skipped when external units or libraries are linked** — they may call
  with the stack convention and were compiled without this knowledge.
- It is not applied to non-`pb36` dialects at all, so the golden output of every
  historic dialect is untouched.
- Caller and callee always flip together, per procedure, in the same compilation.

## Limits

Float and far-pointer arguments in register pairs remain (see
[O0282](O0282-internal-calling-convention.md)); the general internal calling
convention also composes with BYREF collapse and dead-parameter elimination
([O0069](O0069-dead-parameter-elimination.md)) and segment-register allocation
([O0071](O0071-segment-register-allocation.md)).
