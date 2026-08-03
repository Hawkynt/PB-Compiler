# O0064 — `LEA` multiply-add fusion

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter (instruction selection) |
| **Related** | [O0004](O0004-strength-reduction.md), [O0061](O0061-reassociation.md), [C0001](C0001-386-codegen.md) |

## The idea

`LEA` computes an address expression without touching the flags and without
loading anything — which makes it a free three-input adder. Even on the 8086,
`a + b + const` is one `LEA AX,[BX+SI+const]`. Under `$CPU 80386` the scaled
forms turn `x*2+y`, `x*4+y` and `x*8+y` into single instructions, and `x*3`,
`x*5`, `x*9` into one `LEA EAX,[EAX+EAX*n]`.

Chained, that covers the multiplication by 320 the whole corpus does for pixel
addressing: `y*320 + x` in two flag-free instructions that also **leave the
operands intact** for CSE reuse.

## Applies to

```basic
$CPU 80386
DIM x%, y%, o%
o% = y% * 320 + x%
```

## Today

```asm
    mov     ax, [y]
    mov     cl, 6
    shl     ax, cl           ; y*64
    mov     bx, ax
    mov     ax, [y]
    mov     cl, 8
    shl     ax, cl           ; y*256
    add     ax, bx           ; y*320
    add     ax, [x]
    mov     [o], ax
```

## Planned

```asm
    mov     eax, [y]
    lea     eax, [eax+eax*4]     ; y*5
    shl     eax, 6               ; y*320
    add     eax, [x]
    mov     [o], ax
```

## Equivalent BASIC

Unchanged.

## What it needs

- The 32-bit ModRM/SIB encoder for scaled addressing — the same substrate the
  386 register tier needs ([O0058](O0058-386-register-allocation.md)).
- A small multiplier-decomposition search (`LEA` chains vs shift/add chains),
  sharing the cost model with [O0004](O0004-strength-reduction.md).
- The 8086 two-register form is available today but only pays when both operands
  already sit in index registers, which in practice means it waits on register
  allocation as well.
