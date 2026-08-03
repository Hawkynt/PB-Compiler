# O0204 — `INC`/`DEC` for ±1

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Expressions.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF46.BAS` |
| **Split from** | [O0008](O0008-peephole-zero-idiom.md) |

## What it is

An add or subtract of exactly 1 — modular or checked — becomes `INC` or `DEC`:
one byte instead of three.

## Sample

```basic
DIM n%
n% = n% + 1
```

## Without / with

```asm
    mov     ax, [n]          ; without (already immediate-folded)
    add     ax, 0001h        ; 3 bytes
    mov     [n], ax

    mov     ax, [n]          ; with
    inc     ax               ; 1 byte
    mov     [n], ax
```

## Why it is safe

Two flag facts make the substitution exact:

- `INC`/`DEC` **do** set OF, so the `$ERROR OVERFLOW` `JNO` guard is preserved —
  this is the reason the rewrite is legal under checked arithmetic at all;
- they leave **CF** alone, which the add/sub paths never read, so nothing
  downstream can observe the difference.

## See also

For a variable that is not already in a register,
[O0206](O0206-memory-incr-in-place.md) increments the cell directly.
