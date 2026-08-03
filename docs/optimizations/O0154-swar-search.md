# O0154 — SWAR search idioms

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter / runtime |
| **Related** | [O0153](O0153-swar-arithmetic.md), [O0073](O0073-algorithmic-idiom-catalog.md), [R0003](R0003-string-engine.md) |

## The idea

The classic bit-twiddling search idioms, applied to string and array scanning:

- **zero-byte detection** — `(v - 0x0101…) AND ~v AND 0x8080…` is non-zero
  exactly when some byte of `v` is zero; one test covers 2 (or 4) bytes at once;
- **parallel comparison** — XOR the word with a broadcast of the sought byte,
  then apply the zero-byte test: it finds a character in 2/4 bytes per step;
- **mask extraction** — turn the resulting flag word into an index with a bit
  scan.

These make `INSTR`, `LTRIM$`, `LEN` over ASCIIZ and array searches several times
faster **without any SIMD unit** — which matters precisely because the target is
an 8086.

## Applies to

```basic
DIM s$, p%
p% = INSTR(s$, "x")
```

and the array form:

```basic
DIM i%, a(0 TO 999) AS BYTE
FOR i% = 0 TO 999
  IF a(i%) = 32 THEN EXIT FOR
NEXT
```

## Today

One byte compared per iteration, in the runtime and in the generated loop alike.

## Planned

```asm
    mov     ax, [si]         ; two bytes
    xor     ax, cx           ; cx = the sought byte, broadcast
    mov     bx, ax
    sub     ax, 0101h
    not     bx
    and     ax, bx
    and     ax, 8080h        ; non-zero iff one of the two bytes matched
    jnz     Found
```

## What it needs

- The runtime routines are the highest-value place to apply it
  ([R0003](R0003-string-engine.md)) — that is one implementation benefiting
  every program, versus a recognizer that fires occasionally.
- Over-read safety at the end of the buffer
  ([O0139](O0139-alignment-versioning.md)); string descriptors know their
  length, which makes the bound available.
- The match **position** must be derived exactly, including which lane matched
  first, so the result equals the byte-at-a-time scan.
