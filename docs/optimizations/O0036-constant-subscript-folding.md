# O0036 — Constant subscript folding

| | |
|---|---|
| **Status** | ✅ Implemented (static arrays, in-range compile-time subscripts, any rank) |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Arrays.cs` — `TryFoldSubscripts` |
| **Gate** | `--optimize` |
| **Related** | [O0004](O0004-strength-reduction.md), [O0016](O0016-value-fact-analysis.md), [O0030](O0030-induction-variable-strength-reduction.md) |

## What it is

When every subscript of a static array access is a compile-time constant, the
flattened element index — and therefore the element's address — is a compile-time
constant too. The access becomes a bare displacement inside the memory operand:
the whole `MOV AX,k / SHL AX,1 / MOV BX,AX` scale-and-add sequence disappears,
along with the `PUSH`/`POP` pair each extra dimension costs and the staging
around a store.

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

## Equivalent BASIC

```basic
DIM aFlat%(0 TO 9)
aFlat%(7) = 1      ' but with the index resolved at compile time
```

## Why it is safe

The fold applies only to **static** arrays, whose base and bounds are fixed at
compile time, and only when the constant index is **in range**: an out-of-range
constant keeps the ordinary path, where `$ERROR BOUNDS` raises Error 9 and the
unchecked 16-bit address arithmetic wraps exactly as before. (A constant index
outside the declared bounds is in any case already a compile error in genuine
PBC for the common shapes.)
