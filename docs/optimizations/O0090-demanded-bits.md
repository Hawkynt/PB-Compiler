# O0090 — Demanded bits and truncation pushed into the producer

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Mid-end analysis + emitter |
| **Related** | [O0016](O0016-value-fact-analysis.md), [O0057](O0057-storage-narrowing.md), [O0089](O0089-extension-elimination.md) |

## The idea

Compute only the bits that consumers actually observe. If a result is
immediately masked with `AND 255`, or stored into a `BYTE`, the high bits were
computed for nothing — so the producer can be narrowed instead of the result
being truncated afterwards.

This is the dual of [O0016](O0016-value-fact-analysis.md)'s known-bits domain:
that one propagates facts *forward* from operands, this one propagates demand
*backward* from consumers.

## Applies to

```basic
DIM a&, b&, c AS BYTE
c = (a& * b&) AND 255
```

## Today

The full 32-bit product is formed (a runtime `rt_lmul` call or an x87 round
trip), then masked and truncated.

## Planned

Only the low 8 bits are demanded, and the low 8 bits of a product depend only on
the low 8 bits of each operand:

```asm
    mov     al, byte ptr [a]
    mul     byte ptr [b]     ; an 8-bit multiply is enough
    mov     [c], al
```

## What it needs

- A **demanded-bits** backward analysis over the SSA form, with transfer
  functions for `+ - * AND OR XOR NOT`, the shifts and the narrowing stores.
  Multiplication and addition are the interesting cases: the low n bits of the
  result depend only on the low n bits of the operands, which is what makes the
  narrowing sound.
- Division, comparison and any wide *observation* (`PRINT a& * b&`, which PB
  shows in full precision) demand all bits and stop the propagation.
- On x86-16 the emitter still decides whether a narrower operation is actually
  cheaper — see [O0057](O0057-storage-narrowing.md) for why that split matters.
