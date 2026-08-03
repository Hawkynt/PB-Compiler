# O0116 — Loop guard hoisting

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0062](O0062-loop-restructuring.md) (rotation), [O0112](O0112-countdown-loop.md), [O0131](O0131-exact-trip-count.md) |

## The idea

A pre-test `FOR` loop tests the counter before the first iteration *and* after
every increment. Testing the zero-trip case **once** in a guard, then running a
bottom-tested loop, removes one test from the steady state and one unconditional
jump from the entry:

```
guard:  if from > to goto done
top:    body ; i = i + step ; if i <= to goto top
done:
```

This is loop rotation ([O0062](O0062-loop-restructuring.md)) seen from the
emitter side, and it is a prerequisite for
[O0112](O0112-countdown-loop.md).

## Applies to

```basic
DIM i%, n%, s%
FOR i% = 1 TO n%
  s% = s% + i%
NEXT
```

## Today

```asm
    mov     si, 0001h
Top:
    cmp     si, [limit]      ; tested before every iteration, including the first
    jg      Done
    add     di, si
    inc     si
    jmp     Top              ; an unconditional back-edge
Done:
```

## Planned

```asm
    mov     si, 0001h
    cmp     si, [limit]      ; the zero-trip guard, once
    jg      Done
Top:
    add     di, si
    inc     si
    cmp     si, [limit]
    jle     Top              ; conditional back-edge, no separate JMP
Done:
```

## What it needs

- The counter's **end value** must still be exactly what the pre-test loop
  leaves — the increment-then-test value including the 16-bit wrap
  (QUIRK 2.28). Rotation changes where the test happens, never that value.
- The SSA CFG builder currently **bails on post-test loops**, so rotating one
  would take it out of SSA range unless the builder is taught the bottom-tested
  shape first. That dependency is worth calling out: this transform must not
  silently disable [O0017](O0017-sccp.md) for the loop it improves.
