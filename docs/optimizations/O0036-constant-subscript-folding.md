# O0036 — Constant subscript folding

| | |
|---|---|
| **Status** | ✅ Implemented (compile-time subscripts, any rank; a module-level array's element address is loaded as one `MOV reg, OFFSET`) |
| **Stage** | IR middle end (folding) + x86 back end (instruction selection) |
| **Source** | index arithmetic folded by `Ir/Passes/InstCombine.cs` / `Ir/Passes/Sccp.cs`; `Backend/InstructionSelector.cs` — `TryGepDisplacement`, `SelectGlobalGep`, `PointerMemory`; bounds checks decided by `Ir/Passes/RangeCheckElim.cs` |
| **Gate** | `--optimize` |
| **Related** | [O0004](O0004-strength-reduction.md), [O0016](O0016-value-fact-analysis.md), [O0030](O0030-induction-variable-strength-reduction.md) |

## What it is

When every subscript of a static array access is a compile-time constant, the
flattened element index — and therefore the element's address — is a compile-time
constant too. The access becomes a bare displacement inside the memory operand:
the whole `MOV AX,k / SHL AX,1 / MOV BX,AX` scale-and-add sequence disappears,
along with the `PUSH`/`POP` pair each extra dimension costs and the staging
around a store.

In the IR the subscript arithmetic is ordinary integer arithmetic, so constant
folding leaves an element `gep` with a constant byte offset. Instruction
selection turns that offset into a displacement (`TryGepDisplacement`): through
a base register — a frame array, or a descriptor's data pointer — it becomes
`[base+disp]` in the access itself (`PointerMemory`); for a module-level array
`SelectGlobalGep` loads the whole address as one `MOV reg, OFFSET a+disp` and the
access goes through that register.

## Sample

```basic
DIM a%(0 TO 9), g%(0 TO 3, 0 TO 3)
a%(7) = 1
g%(2, 3) = a%(7)
```

## Without the optimizer

```asm
    mov     ax, 0007h
    shl     ax, 1
    mov     bx, ax
    mov     ax, 0001h
    mov     [a+bx], ax
    mov     ax, 0002h        ; row
    push    ax
    mov     ax, 0003h        ; column
    pop     bx
    ...                      ; row*4 + column, scaled
    mov     bx, ax
    ...
```

## With the optimizer

```asm
    mov     word ptr [a+14], 0001h    ; element 7 -> +14 bytes
    mov     ax, [a+14]
    mov     [g+22], ax                ; (2,3) -> element 11 -> +22 bytes
```

(For these module-level arrays the current back end forms each address as
`MOV reg, OFFSET a+14` and accesses `[reg]`; the scale-and-add sequence is gone
either way.)

## Equivalent BASIC

```basic
DIM aFlat%(0 TO 9)
aFlat%(7) = 1      ' but with the index resolved at compile time
```

## Why it is safe

Only the address arithmetic is folded; the access itself is unchanged. Under
`$ERROR BOUNDS` the check is an explicit compare and a branch to the raise in
the IR: a constant index proven in range lets `RangeCheckElim`/SCCP remove it,
and an out-of-range constant makes the raise unconditional, so Error 9 is still
raised. (A constant index outside the declared bounds is in any case already a
compile error in genuine PBC for the common shapes.)
