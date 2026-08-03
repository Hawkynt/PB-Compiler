# O0112 — Countdown loop with `DEC`/`JNZ`

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0005](O0005-register-residency.md), [O0111](O0111-redundant-induction-variables.md), [O0113](O0113-loop-bounds-hoisted.md) |

## The idea

A fixed-trip loop whose counter value is not observed inside the body can count
**down to zero** instead of up to a limit: the decrement sets ZF itself, so the
compare disappears and the loop control becomes two instructions.

## Applies to

```basic
$OPTIMIZE SPEED
DIM i%, s%
FOR i% = 1 TO 100
  s% = s% + 7                ' i% is not read
NEXT
```

## Today

```asm
    mov     si, 0001h
Top:
    cmp     si, 0064h        ; compare against the limit
    jg      Done
    add     di, 0007h
    inc     si
    jmp     Top
Done:
    mov     [i], si
```

## Planned

```asm
    mov     cx, 0064h
Top:
    add     di, 0007h
    dec     cx               ; sets ZF
    jnz     Top
    mov     word ptr [i], 0065h   ; the observable end value, stored once
```

Four instructions per iteration become three, and one of them is no longer a
compare.

## What it needs

- Proof that the counter is **not read** in the body (or that every read can be
  rewritten in terms of the countdown value) — and that the loop's trip count is
  computable, which [O0131](O0131-exact-trip-count.md) formalizes.
- PB's **increment-then-test end value** (QUIRK 2.28, wrap included) must still
  be stored to the counter cell on exit — that is what makes this a rewrite of
  the *mechanism* rather than of the semantics.
- `LOOP CX` is not the answer on a 486+, where `DEC`/`JNZ` is faster than the
  microcoded `LOOP` ([C0002](C0002-486-codegen.md)).
