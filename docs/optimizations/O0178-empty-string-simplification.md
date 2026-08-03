# O0178 — Empty-string operation simplification

| | |
|---|---|
| **Status** | ✅ Done — the `+ ""` concat identities fold and `s$ = ""` is already allocation-free; the zero-length intrinsics are split out as [O0266](O0266-zero-length-intrinsic-folding.md) |
| **Stage** | Emitter |
| **Related** | [O0009](O0009-string-temp-economy.md), [O0024](O0024-multi-concat.md), [O0076](O0076-algebraic-identities.md), [O0181](O0181-empty-string-comparison.md) |
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

## Now (the `+ ""` identities — done)

```asm
    ; t$ = s$ + ""   ->   t$ = s$
    mov     ax, [s]
    call    StrDup           ; the copy reading s$ already makes - no StrCat
    call    StrAssign
```

`EmitStringBinary` folds `x$ + ""` and `"" + x$` to just evaluating `x$`, and
`FlattenStringConcat` (the [O0024](O0024-multi-concat.md) chain builder) drops
any empty-literal leaf before it stages operands. So `a$ + "" + b$` builds from
two operands, and `"" + a$ + "" + b$ + ""` from two as well.

### Why it is sound

Reading *any* string expression already yields an **owned** handle — a variable
`StrDup`s, a literal/function-result/temp is owned by construction — which is
exactly what `StrCat(x$, "")` would produce, only without the redundant copy and
the two frees of a zero-length operand. So the result is an *independent* copy,
not an alias (`t$ = a$ + ""` then `a$ = "…"` leaves `t$` unchanged — verified by
self-differential run against the golden-faithful build). The empty literal has
no side effect, so dropping it never reorders evaluation.

Native-only, in `CodeGenerator.EmitStringBinary` / `FlattenStringConcat`. The IR
back ends lower concatenation to `rt_strcat`/`rt_strcatn` calls whose empty
operand the host C compiler folds away, so no dedicated IR pass is needed.

## `s$ = ""` assignment — already allocation-free

```asm
    ; s$ = ""
    xor     ax, ax           ; the empty literal IS handle 0 - no StrMem
    lea     bx, [s]
    call    StrAssign        ; frees the old handle, stores 0
```

No temp is ever allocated: `EmitStringLiteral("")` emits `xor ax, ax`, and the
store path hands that 0 to `StrAssign`, whose free-old-then-store *is* the
`StrFree` + `mov [s],0` the "planned" sketch above wanted — releasing the old
handle is necessary work, not overhead. (Skipping even that `StrAssign` when the
slot is *provably* already empty is a known-empty value-fact refinement, not part
of this identity.)

## Split off

- **Zero-length intrinsics** (`LEFT$(s$,0)`, `MID$(s$,i,0)`, `SPACE$(0)`,
  `STRING$(0,c)` → the empty string, no call) — tracked separately as
  [O0266](O0266-zero-length-intrinsic-folding.md).
