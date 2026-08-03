# O0009 — String-temp economy

| | |
|---|---|
| **Status** | ✅ Implemented (closed — literal folding, self-append in place, chain dead-temp reuse) |
| **Stage** | Emitter + string runtime |
| **Source** | `CodeGen/CodeGenerator.Optimize.cs` (`TryEmitFolded` string arm), `Runtime/DosRuntime` — `rt_strcatlit`, `rt_strcatvar` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF83.BAS`, `DIFF94.BAS`, `DIFF95.BAS`, `DIFF97.BAS` |
| **Related** | [O0011](O0011-literal-overlap-pooling.md), [O0024](O0024-multi-concat.md) |
| **Split into** | [O0207](O0207-self-concat-handle-reuse.md), [O0208](O0208-inplace-literal-append.md), [O0209](O0209-inplace-variable-append.md), [O0210](O0210-concat-chain-temp-reuse.md) |

## What it is

PB builds a fresh heap temp for every string expression node.

**This page covers literal concat folding**: `+`/`&` over literals and string
equates folds into one pooled literal at compile time, so no temp exists at run
time at all. The `ConstantFolder` also folds `&` for string equates.

The in-place and handle-reuse forms each have their own entry (see *Split into*
above).

## Sample

```basic
DIM s$, i%
s$ = "<" + "html" + ">"      ' (1)
FOR i% = 1 TO 100
  s$ = s$ + "x"              ' (3)
NEXT
PRINT LEN(s$)
```

## Without the optimizer

`"<" + "html" + ">"` allocates two temps and copies three times at run time, and
each loop iteration allocates a new block and copies the whole accumulated
string into it:

```asm
    ; per iteration
    call    StrDup           ; copy s$
    call    StrMem           ; allocate len(s$)+1
    call    StrCat           ; copy both operands into it
    call    StrAssign        ; free the old handle
```

100 iterations ⇒ 100 allocations and ~5 000 bytes copied.

## With the optimizer

```asm
    mov     dx, offset lit_html   ; "<html>" — one pooled literal, folded
    call    StrAssign
    ; per iteration
    call    rt_strcatlit          ; grows the topmost block in place, same handle
```

100 iterations ⇒ 0 extra allocations and 100 bytes copied.

## Equivalent BASIC

```basic
DIM s$, i%
s$ = "<html>"
FOR i% = 1 TO 100 : s$ = s$ + "x" : NEXT     ' but with a StringBuilder's cost
PRINT LEN(s$)
```

## Why it is safe

The in-place paths check **topmost-ness** at run time and fall back to the exact
`StrMem` + `StrCat` sequence otherwise, so the resulting string and the freed
temporaries are identical either way — only the allocation count differs. For
`s$ = s$ + s$` the destination begins exactly where the source ends and
`REP MOVSB` copies forward, so no byte is read after being overwritten. The
`$STRING` cap is honored before any in-place growth.

## Limits

Operands that share a volatile buffer — the result of a `FUNCTION` or a string
intrinsic — are excluded for **soundness**, not economy: staging them would
alias (`f$() & g$() & h$()` would read `"hhh"`), so the pairwise
consume-immediately path is the optimal sound strategy there. Chains of three or
more safe operands go to [O0024](O0024-multi-concat.md), which does strictly
better with a single allocation.
