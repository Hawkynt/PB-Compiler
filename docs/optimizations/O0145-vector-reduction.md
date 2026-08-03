# O0145 — Vector reduction

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0026](O0026-auto-vectorization.md), [O0119](O0119-reduction-recognition.md), [O0120](O0120-multiple-accumulators.md) |

## The idea

A reduction loop (`s = s + a(i)`) vectorizes by keeping the accumulator
**packed**: each lane accumulates its own partial result, and a single
horizontal combine after the loop produces the scalar answer. With two or four
vector accumulators the vector-add latency is hidden as well.

This is the single most common loop shape in numeric BASIC code and the most
valuable extension of [O0026](O0026-auto-vectorization.md), which today handles
only elementwise `c(i) = a(i) OP b(i)`.

## Applies to

```basic
$CPU 80586 MMX
$OPTIMIZE SPEED
DIM i%, s%, a%(0 TO 999)
FOR i% = 0 TO 999
  s% = s% + a%(i%)
NEXT
```

## Today

The scalar accumulate loop — already excellent
([O0005](O0005-register-residency.md) +
[O0030](O0030-induction-variable-strength-reduction.md) give
`ADD DI,[BX] / ADD BX,2 / ADD SI,1`), but one element per iteration.

## Planned

```asm
    pxor    mm1, mm1              ; four partial sums
Top:
    paddw   mm1, [si]
    add     si, 8
    dec     cx
    jnz     Top
    ; horizontal combine: fold the four lanes into one
    movq    mm0, mm1
    psrlq   mm0, 32
    paddw   mm1, mm0
    movq    mm0, mm1
    psrlq   mm0, 16
    paddw   mm1, mm0
    movd    ax, mm1
    emms
```

## What it needs

- [O0119](O0119-reduction-recognition.md) to classify the reduction and supply
  the identity element for the vector accumulator.
- The **legality argument**: splitting a sum across lanes is exact because
  16-bit addition is associative modulo 2¹⁶ — each lane's partial sum wraps
  exactly as the scalar accumulator would, and the horizontal combine wraps the
  same way. This does **not** hold for a float reduction.
- A scalar tail ([O0146](O0146-vector-tail.md)) and the `$ERROR`-off gate the
  existing vectorizer uses.
