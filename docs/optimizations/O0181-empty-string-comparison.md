# O0181 — Empty-string comparison via length

| | |
|---|---|
| **Status** | ⬜ Planned |
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

## Planned

```asm
    cmp     word ptr [s], 0000h     ; an unassigned/empty handle
    jne     Continue
```

— and with [O0031](O0031-branch-fusion.md) the flags drive the loop's branch
directly.

## What it needs

- The **representation invariant**: an empty string must be *exactly* handle 0,
  or the test needs the length instead (a one-indirection read, still far
  cheaper than `StrCmp`). The runtime's rules for a freshly zeroed slot versus
  an explicitly emptied one have to agree — which is also why
  [O0019](O0019-zero-elision.md) keeps string handle slots zeroed even when it
  elides the rest of the frame fill.
- Fixed-length and ASCIIZ strings compare differently (padding), so the rewrite
  applies to dynamic strings only.
