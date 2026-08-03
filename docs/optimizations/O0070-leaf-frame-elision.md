# O0070 — Leaf-frame elision

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter (procedure prologue/epilogue) |
| **Related** | [O0019](O0019-zero-elision.md), [O0021](O0021-register-parameters.md), [O0006](O0006-inlining.md) |

## The idea

[O0019](O0019-zero-elision.md) removes the frame *zeroing*. The next step is to
remove the **frame**: a `SUB`/`FUNCTION` with no locals, no strings and no
`GOSUB` needs neither the `PUSH BP` / `MOV BP,SP` prologue nor the
`MOV SP,BP` / `POP BP` epilogue — its parameters can be addressed from SP, or
(with [O0021](O0021-register-parameters.md)) live in registers and need no
addressing at all.

Three instructions and a stack slot per call, on exactly the small procedures
that get called most.

## Applies to

```basic
$OPTIMIZE SPEED
FUNCTION Clamp%(BYVAL v%, BYVAL hi%)
  IF v% > hi% THEN Clamp% = hi% ELSE Clamp% = v%
END FUNCTION
```

## Today

```asm
Clamp:
    push    bp
    mov     bp, sp
    mov     ax, [bp+6]
    cmp     ax, [bp+4]
    jle     Below
    mov     ax, [bp+4]
Below:
    mov     sp, bp
    pop     bp
    ret     4
```

## Planned (with register parameters)

```asm
Clamp:
    cmp     ax, dx
    jle     Below
    mov     ax, dx
Below:
    ret
```

## Equivalent BASIC

Unchanged — an ABI/prologue decision.

## What it needs

- A proof that nothing in the body needs a frame pointer: no locals, no dynamic
  strings or FLEX values whose handles the epilogue must free, no `GOSUB` return
  addresses, no `ON ERROR` (a handler re-entry needs a well-formed frame), no
  inline asm referencing `[BP+…]`, and no variable-length stack traffic.
- SP-relative parameter addressing (or full register parameters) so the
  arguments stay reachable while the stack moves.
