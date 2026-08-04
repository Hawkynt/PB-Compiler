# O0181 — Empty-string comparison via length

| | |
|---|---|
| **Status** | ✅ Done |
| **Stage** | Emitter |
| **Related** | [O0178](O0178-empty-string-simplification.md), [O0180](O0180-string-length-caching.md), [O0031](O0031-branch-fusion.md) |

## The idea

`s$ = ""` and `s$ <> ""` are the commonest string comparisons in DOS-era code —
every `INPUT` loop ends with one. Lowering them to a full `StrCmp` call is
wasteful: a string is empty exactly when its **handle is zero** (or its length
is), so the test is a compare against zero.

## Applies to

```basic
DIM s$
DO
  LINE INPUT s$
LOOP UNTIL s$ = ""
```

## Today

```asm
    mov     ax, [s]
    push    ax
    mov     dx, offset emptyLit
    push    dx
    call    StrCmp           ; a full comparison routine
    or      ax, ax
    jnz     Continue
```

## Now

```asm
    mov     ax, [s]
    or      ax, ax                  ; ZF set iff the handle is 0 - the empty string
    jne     Continue
```

— and with [O0031](O0031-branch-fusion.md) the `OR`'s flags drive the loop's
branch directly (`TryEmitCompareAsBranch`), so no truth value is materialized.
When the comparison is used as a value instead of a branch, the ZF is turned
into `0`/`-1` by the ordinary [O0088](O0088-branchless-boolean.md) path.

The **`LEN(s$) = 0` / `LEN(s$) <> 0`** spelling of the same test folds to the same
handle test (`TryEmitLenEmptyTest`), collapsing the `rt_len` call and its compare
to `OR AX,AX` — `LEN(s$) = 0` byte-identical to `s$ = ""`. Restricted to `=`/`<>`
against a literal `0`: those are exact whatever a length past 32767 would do under
a *signed* relational compare, where `LEN(s$) > 0` would not be, so the relational
forms keep the `rt_len` path. Verified by a DOSBox self-diff over an empty and a
non-empty runtime string and an `absent-call rt_len` byte assertion.

## What made it safe

- The **representation invariant** holds unconditionally: `rt_stralloc`
  returns handle **0** for length 0, and every empty-producing path
  (`MID$(x,i,0)`, `""`, `LEFT$(x,0)`, a freshly zeroed slot) yields the same
  0 handle — verified against the genuine 3.50 oracle (a loop mixing `s = ""`,
  `s <> ""` and `MID$(...,0) = ""` is byte-identical). This is also why
  [O0019](O0019-zero-elision.md) keeps string handle slots zeroed even when it
  elides the rest of the frame fill.
- Restricted to a dynamic-string **variable** against the empty literal
  (`NameExpr` vs `""`): a concat temporary would need freeing, and fixed-length
  / ASCIIZ strings compare with padding, so neither is folded.
- Being the program's only string comparison, the rewrite lets the runtime
  trimmer drop `rt_strcmp` entirely — the regression test
  (`Emit_GivenEmptyStringComparison…`) asserts the `""` image is smaller than
  the otherwise-identical non-empty-literal image that keeps the call.

## Targets

Native x86-16 only, in `CodeGenerator.EmitStringBinary`. The IR back ends
(`--emit-c` / `--emit-llvm`) model strings through the `rt_*` ABI and lower a
`= ""` comparison to a `rt_strcmp(...) == 0` call; on those targets the host C
compiler already reduces `strcmp(s,"")` (or the length check) to an emptiness
test, so a dedicated IR pass would be redundant. Should IR string comparisons
ever be lowered structurally, the same handle/length test applies.
