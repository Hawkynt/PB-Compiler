# O0021 — Register parameters

| | |
|---|---|
| **Status** | ✅ Implemented (leading word-sized `BYVAL` scalars, AX/DX/BX/CX) |
| **Stage** | Whole-program analysis + emitter (both sides of the call) |
| **Source** | `CodeGen/OptRegParm.cs`, `CodeGen/CodeGenerator.Procs.cs` (`ConventionRegisters`) |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED`, `pb36` only |
| **Related** | [O0006](O0006-inlining.md), [O0018](O0018-interprocedural-constant-propagation.md), [docs/LINKER.md](../LINKER.md) (`WATCALL`) |

## What it is

When the compiler owns **every** call site of a procedure — it is defined in a
self-contained module and its address is never taken via `CODEPTR`/`CALL DWORD`
— its leading word-sized `BYVAL` scalar parameters travel in registers
(AX, DX, BX, CX) instead of on the stack, reusing the existing `WATCALL`
lowering. Caller and callee flip together, so the behavior is identical and the
per-call push/pop traffic disappears along with the frame slots.

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

- The optimization is **disabled wholesale** if any procedure address is taken:
  an indirect call could reach the procedure with the stack convention.
- It is **skipped when external units or libraries are linked** — they may call
  with the stack convention and were compiled without this knowledge.
- It is not applied to non-`pb36` dialects at all, so the golden output of every
  historic dialect is untouched.
- Caller and callee always flip together, per procedure, in the same compilation.

## Limits

LONG, float and pointer arguments in register *pairs* are the remaining piece
(see the roadmap item "full register arg-size rules" in `docs/ROADMAP.md`); the
general internal calling convention — BYREF collapse after inlining, segment
register pinning — is [O0071](O0071-segment-register-allocation.md) and
[O0069](O0069-dead-parameter-elimination.md).
