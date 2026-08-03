# O0026 — Auto-vectorization (MMX / SSE2 / AVX / AVX-512)

| | |
|---|---|
| **Status** | ✅ Implemented (elementwise loops over 2-byte arrays; MMX execution-verified, wider widths encoding-verified) |
| **Stage** | Emitter (loop recognizer) |
| **Source** | `CodeGen/CodeGenerator.Optimize.cs` (loop recognizer), `Asm/Assembler.Simd.cs` |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` + `$CPU 80586 MMX` (or `SSE`, `AVX`, `AVX512`) |
| **Related** | [R0004](R0004-asm-intrinsics.md), [O0074](O0074-wider-vectorization.md) |

## What it is

A constant-trip loop of the shape

```basic
FOR i = lo TO hi : c(i) = a(i) OP b(i) : NEXT      ' OP is + - AND OR XOR *
```

over rank-1 static **2-byte-element** arrays is rewritten to process as many
lanes per iteration as the widest available vector register allows:

| `$CPU` feature | register | width | lanes | encoding |
|---|---|---|---|---|
| `MMX` | `MM0` | 64-bit | 4 | legacy `0F` (+ `EMMS`) |
| `SSE2` | `XMM0` | 128-bit | 8 | `66 0F` |
| `AVX2` | `YMM0` | 256-bit | 16 | 2-byte VEX `C5` |
| `AVX512` | `ZMM0` | 512-bit | 32 | 4-byte EVEX `62` |

The body becomes `load a[i..]` · `vec = a OP b[i..]` · `store c[i..]`, stepping
three pointers by the vector width. MMX/SSE2 use the two-operand `Pxxx` form
(dest = dest OP src); AVX/AVX-512 the three-operand `VPxxx`. A fully-unrolled
scalar tail handles the last `n MOD lanes` elements, and the counter is left at
its `FOR` end value.

## Sample

```basic
$CPU 80586 MMX
$OPTIMIZE SPEED
DIM a%(0 TO 63), b%(0 TO 63), c%(0 TO 63), i%
FOR i% = 0 TO 63
  c%(i%) = a%(i%) + b%(i%)
NEXT
```

## Without the optimizer

64 iterations, each with a bounds/scale computation, two loads, an add and a
store:

```asm
Top:
    mov     ax, [i]
    cmp     ax, 003Fh
    jg      Done
    shl     ax, 1
    mov     bx, ax
    mov     ax, [a+bx]
    add     ax, [b+bx]
    mov     [c+bx], ax
    inc     word ptr [i]
    jmp     Top
Done:
```

## With the optimizer

16 iterations of four lanes each:

```asm
    lea     si, [a]
    lea     di, [b]
    lea     bx, [c]
    mov     cx, 0010h
Top:
    movq    mm0, [si]
    paddw   mm0, [di]        ; four 16-bit lanes at once
    movq    [bx], mm0
    add     si, 8
    add     di, 8
    add     bx, 8
    loop    Top
    emms
    mov     word ptr [i], 0040h
```

## Equivalent BASIC

```basic
FOR i% = 0 TO 63 STEP 4
  ' four elements computed simultaneously
  c%(i%) = a%(i%) + b%(i%) : c%(i%+1) = a%(i%+1) + b%(i%+1)
  c%(i%+2) = a%(i%+2) + b%(i%+2) : c%(i%+3) = a%(i%+3) + b%(i%+3)
NEXT
```

## Why it is safe

Every operation is **wrap-correct per 16-bit lane**: `PADDW`, `PSUBW`, `PAND`,
`POR`, `PXOR` and `PMULLW` all wrap mod 2¹⁶ exactly as the scalar `INTEGER`/
`WORD` ALU would, so the vectorized result is byte-identical to the scalar loop.
The pass is gated off when `$ERROR` checking is active (a per-element trap must
still fire in element order) or when SI/DI are register-resident, and loops with
fewer than 8 trips stay scalar. `EMMS` is emitted before any float use can
follow.

## Limits

Reductions, `a(i) OP scalar`, non-2-byte elements and variable trip counts are
[O0074](O0074-wider-vectorization.md). Only the MMX path executes under DOSBox;
the XMM/YMM/ZMM encodings are verified by assembler unit tests against
hand-computed VEX/EVEX opcodes.
