# O0071 — Segment-register allocation

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0005](O0005-register-residency.md), [O0015](O0015-udt-zero-cost.md), [R0003](R0003-string-engine.md) |

## The idea

On x86-16 the segment registers are a register class that a modern compiler
never gets to play with. Today ES is reloaded per access to the string/array
heap; keeping it **pinned across a run of statements** removes a `PUSH DS` /
`POP ES` pair (or a `MOV AX,seg` / `MOV ES,AX`) from every one of them.

The same reasoning applies to a mode-13h graphics loop pinning ES to `A000h`,
and to a `DEF SEG`-based `PEEK`/`POKE` run.

## Applies to

```basic
DIM a$, b$, c$
a$ = b$ + c$
a$ = a$ + "x"
a$ = a$ + b$
```

## Today

Each string operation reloads the heap segment before its `REP MOVSB`:

```asm
    mov     ax, [rt_strseg]
    mov     es, ax
    rep     movsb
    mov     ax, [rt_strseg]      ; again
    mov     es, ax
    rep     movsb
```

## Planned

```asm
    mov     ax, [rt_strseg]
    mov     es, ax               ; pinned for the whole run
    rep     movsb
    rep     movsb
```

## Equivalent BASIC

Unchanged.

## What it needs

- A liveness model for ES (and optionally DS) across a statement run, with every
  barrier that can change it: a `DEF SEG` statement, a `PEEK`/`POKE`, inline
  asm, a `CALL INTERRUPT`, an external call, a heap compaction inside the string
  runtime, and any `REP` op that sets ES itself.
- Interaction with `ON ERROR`: the handler must not observe a pinned ES that its
  own path did not establish.
