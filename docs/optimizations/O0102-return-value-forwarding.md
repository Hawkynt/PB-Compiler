# O0102 — Return-value forwarding

| | |
|---|---|
| **Status** | ⬜ Planned (the trivial single-expression `FUNCTION` already skips the temp when it is *inlined* — [O0006](O0006-inlining.md)) |
| **Stage** | Emitter |
| **Related** | [O0006](O0006-inlining.md), [O0027](O0027-copy-propagation.md), [O0070](O0070-leaf-frame-elision.md) |

## The idea

A `FUNCTION`'s result is written to the result slot (the pseudo-variable named
like the function) and loaded again by the epilogue. When the final assignment
to the result is the last statement on its path, the expression should be
computed **directly into the return register**, and the slot never written.

## Applies to

```basic
FUNCTION Scale%(BYVAL v%)
  LOCAL t%
  t% = v% * 3
  Scale% = t% + 1
END FUNCTION
```

## Today

```asm
    mov     ax, [bp-2]       ; t%
    inc     ax
    mov     [bp-4], ax       ; the result slot
    ...
    mov     ax, [bp-4]       ; epilogue reload
    mov     sp, bp
    pop     bp
    ret     2
```

## Planned

```asm
    mov     ax, [bp-2]
    inc     ax               ; already in the return register
    mov     sp, bp
    pop     bp
    ret     2
```

## What it needs

- A check that the result slot is not read again on any path after the
  assignment (PB allows reading the result pseudo-variable), and that no `EXIT
  FUNCTION` elsewhere expects the slot to hold a partial value.
- String and float results have their own return protocols (a handle, the FPU
  stack), so each result kind needs its own forwarding rule.
- It composes with [O0103](O0103-shared-epilogue.md): with several exits, the
  forwarding must hold on every path that reaches the epilogue.
