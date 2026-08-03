# O0207 — Self-concat handle reuse

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator` (string assignment path) |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF83.BAS` |
| **Split from** | [O0009](O0009-string-temp-economy.md) (which is now literal concat folding) |

## What it is

For `s$ = s$ + rhs` (append) or `s$ = lhs + s$` (prepend), where the other
operand is a string literal or a bare variable, `s$`'s handle is passed straight
to `StrCat` — which copies both operands and then frees them — and the result is
stored.

That drops the redundant `StrDup` of `s$` **and** the `StrAssign` free: one
fewer full copy of the growing string per concatenation, with no change to the
heap routines.

## Sample

```basic
DIM s$, v$
s$ = s$ + v$
```

## Without / with

```asm
    call    StrDup           ; without: copy s$ first
    call    StrMem
    call    StrCat
    call    StrAssign        ; free the old handle

    call    StrCat           ; with: s$'s handle goes straight in
```

## Why it is safe

`StrCat` consumes both operand handles exactly as the pairwise path did, so the
ownership bookkeeping is unchanged — only the redundant copy of a value that was
about to be freed disappears.

## See also

The in-place growth forms that avoid the allocation entirely:
[O0208](O0208-inplace-literal-append.md) and
[O0209](O0209-inplace-variable-append.md).
