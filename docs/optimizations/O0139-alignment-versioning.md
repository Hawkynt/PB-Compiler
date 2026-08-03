# O0139 — Alignment peeling and access versioning

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0115](O0115-loop-peeling.md), [O0137](O0137-load-widening.md), [O0026](O0026-auto-vectorization.md) |
| **Split into** | [O0251](O0251-misaligned-versioning.md), [O0252](O0252-safe-overread-versioning.md) |

## The idea

Wide and vector accesses want aligned, in-bounds memory. Three techniques make
that true rather than assumed:

1. **Alignment peeling** — run scalar iterations until the pointer reaches the
   required boundary, then enter the aligned main loop;
2. **Misaligned versioning** — emit an aligned fast path and an unaligned (or
   scalar) fallback, chosen by a runtime test on the pointer's low bits;
3. **Safe over-read versioning** — permit a widened load past the last element
   only when there is provably accessible padding behind the data; otherwise use
   a scalar tail.

The third is the one that turns "usually works" into "always correct": reading
four bytes where two remain is a bounds violation or, at a segment boundary, a
genuine fault.

## Applies to

```basic
$CPU 80586 MMX
$OPTIMIZE SPEED
DIM i%, n%, a%(0 TO 999), b%(0 TO 999), c%(0 TO 999)
FOR i% = 0 TO n%
  c%(i%) = a%(i%) + b%(i%)
NEXT
```

## Planned

```asm
    ; peel until (a + i*2) is 8-byte aligned
PeelTop:
    test    si, 0007h
    jz      Aligned
    ...                      ; one scalar element
    jmp     PeelTop
Aligned:
    ...                      ; the aligned vector kernel
    ...                      ; scalar tail for the remainder
```

## What it needs

- Static alignment facts where they exist (a static array's base is known at
  link time; a dynamic one's is not), and the runtime test where they do not.
- The **congruence domain** from [O0016](O0016-value-fact-analysis.md) is
  exactly the right tool for "this pointer is ≡ 0 (mod 8)".
- A tail strategy ([O0146](O0146-vector-tail.md)), because peeling only fixes
  the head.
