# O0112 — Countdown loop with `DEC`/`JNZ`

| | |
|---|---|
| **Status** | ✅ Done (constant-bound Int16 register-counter loops; the DI accumulator is kept) |
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

## How it works

Inside the SI-counter path (`TryEmitForCounterInRegister`), when the counter is
**not read** anywhere in the body (`BodyReadsVariable` — as a value *or* an array
subscript), there is no stepped element pointer, and the bounds are compile-time
constants, SI is loaded with the **trip count** and counted down (`DEC SI`/`JNZ`).
The DI accumulator (if any) is untouched — it does not depend on the counter — so
the hot `acc = acc + …` loop keeps its register residency and *also* loses the
compare. On exit the observable **increment-then-test end value** (QUIRK 2.28) is
stored to the counter cell as a single immediate.

### The wrap edge

The rewrite fires only when the true end value `from + trips*step` lands **inside**
INTEGER range. That is exactly the guard against the wrapping FOR: `i% = 1 TO
32767 STEP 1` reaches `32768`, which overflows and, in PB, wraps back below the
limit and loops **forever** — computing a finite trip count there would be wrong.
When the end value is out of range the countdown declines and the ordinary
top-tested compare path reproduces PB's behaviour exactly (confirmed: that loop
keeps its `CMP SI` compare).

Verified byte-identical against the genuine oracle over ascending, descending
`STEP`, and zero-trip loops (end values `101`, `2`, `5` all correct); a regression
test confirms a count-only loop emits no limit compare while an `i`-reading loop
keeps it. Constant bounds only for now — a runtime trip count would need a divide
for `STEP > 1`.
