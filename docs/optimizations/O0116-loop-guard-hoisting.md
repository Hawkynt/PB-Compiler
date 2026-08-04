# O0116 — Loop guard hoisting

| | |
|---|---|
| **Status** | ✅ Done (this is the emitter view of the [O0062](O0062-loop-restructuring.md) rotation, implemented on every FOR path) |
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

## Now

The rotation ships under `$OPTIMIZE SPEED` on **every** FOR emission path — the
SI-resident counter (`TryEmitForCounterInRegister`), the memory-counter fallback
(`EmitForInt16Fast`), the 386 LONG counter (`TryEmitForLongCounterInRegister`),
the nested DI-resident inner loop, and the pre-tested `DO`/`WHILE`
(`EmitDoLoopControl`). Each emits the zero-trip guard once, then a bottom-tested
loop whose conditional back-edge replaces the unconditional `JMP`. The doc's exact
example (`FOR i% = 1 TO n%`, a variable limit) emits the two compares — the entry
guard and the bottom test — with no separate jump; a differential checksum and the
`Emit_GivenRegisterCounterFor` / `Emit_GivenPreTestedDoLoop` regression tests pin
the two-ended shape.

### Correctness held

- The counter's **end value** stays exactly what the pre-test loop leaves — the
  increment-then-test value including the 16-bit wrap (QUIRK 2.28). Rotation
  changes where the test happens, never that value; the compare still runs the
  same N+1 times.
- Gated on `--optimize`, so the faithful build keeps the top-tested form
  byte-identical to genuine (golden gate 250/250).
