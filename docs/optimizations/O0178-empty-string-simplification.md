# O0178 — Empty-string operation simplification

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0009](O0009-string-temp-economy.md), [O0024](O0024-multi-concat.md), [O0076](O0076-algebraic-identities.md) |
| **Split into** | [O0266](O0266-zero-length-intrinsic-folding.md) |

## The idea

The string identities, which are the string counterpart of
[O0076](O0076-algebraic-identities.md):

| Source | Result |
|---|---|
| `s$ + ""`, `"" + s$` | `s$` |
| `s$ = ""` (assignment) | free the old handle, store 0 — no allocation |
| `LEFT$(s$, 0)`, `MID$(s$, i, 0)`, `SPACE$(0)`, `STRING$(0, c)` | the empty string, no call |
| `s$ & ""` inside a concat chain | the operand drops out of the chain |

Each of these currently goes through the heap: a `StrMem` allocation, a
`StrCat`, a temp, and a free.

## Applies to

```basic
DIM s$, t$
t$ = s$ + ""
s$ = ""
```

## Today

```asm
    ; t$ = s$ + ""
    call    StrDup           ; copy s$
    call    StrMem           ; allocate len+0
    call    StrCat           ; copy both operands
    call    StrAssign
```

## Planned

```asm
    ; t$ = s$
    call    StrAssign        ; one handle assignment
    ; s$ = ""
    call    StrFree
    mov     word ptr [s], 0000h
```

## What it needs

- The **ownership rules** are the whole difficulty: `t$ = s$ + ""` must still
  produce an independent copy if `t$` and `s$` are separately assignable
  afterwards, so the identity is "concatenation with empty" → "assignment", not
  "→ alias".
- Empty operands are already handled correctly *inside*
  [O0024](O0024-multi-concat.md) (handle 0 reads length 0, copies nothing, frees
  nothing) — this is about not entering the machinery at all.
