# O0026 — Auto-vectorization (MMX / SSE2 / AVX / AVX-512)

| | |
|---|---|
| **Status** | ✅ Implemented (elementwise loops over 2-byte arrays; MMX execution-verified, wider widths encoding-verified) |
| **Stage** | IR (native pipeline, after the standard middle end) + runtime kernels |
| **Source** | `Ir/Passes/PackedLoopVectorization.cs` (run from `IrMiddleEndPipeline.RunNativeModule`); `Runtime/DosRuntime.Packed.cs` — `EmitPackedKernel`; `Asm/Assembler.Simd.cs` |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` + `$CPU 80586 MMX` (or `SSE`, `AVX`, `AVX512`) |
| **Related** | [R0004](R0004-asm-intrinsics.md), [O0074](O0074-wider-vectorization.md) |

## What it is

A constant-trip loop of the shape

```basic
FOR i = lo TO hi : c(i) = a(i) OP b(i) : NEXT      ' OP is + - AND OR XOR *
```

over near **2-byte-element** arrays is replaced by one call to a packed runtime
kernel (`rt_packed16_add`, `_sub`, `_and`, `_or`, `_xor`, `_mul`), which the
DOS runtime emits for the widest available vector register:

| `$CPU` feature | register | width | lanes | encoding |
|---|---|---|---|---|
| `MMX` | `MM0` | 64-bit | 4 | legacy `0F` (+ `EMMS`) |
| `SSE2` | `XMM0` | 128-bit | 8 | `66 0F` |
| `AVX2` | `YMM0` | 256-bit | 16 | 2-byte VEX `C5` |
| `AVX512` | `ZMM0` | 512-bit | 32 | 4-byte EVEX `62` |

`PackedLoopVectorization` recognizes the loop in SSA, after inlining and
propagation have exposed it: a header holding only the counter and its compare
against a constant limit, a constant start, and a one-block body that stores
once, loads at most twice, calls nothing, and addresses near memory stepping by
two bytes. It puts the call in the preheader with the three start addresses and
the trip count, and leaves the counter at its `FOR` end value for any later
reader. The kernel's body is `load a[i..]` · `vec = a OP b[i..]` ·
`store c[i..]`, stepping three pointers by the vector width. MMX/SSE2 use the
two-operand `Pxxx` form (dest = dest OP src); AVX/AVX-512 the three-operand
`VPxxx`. A scalar loop handles the last `n MOD lanes` elements.

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

One call; the kernel runs 16 iterations of four lanes each:

```asm
    ...                      ; DI -> c, BX -> a, SI -> b, CX = 64
    call    rt_packed16_add
    ...
rt_packed16_add:             ; MMX build, shown for whole vectors
Top:
    movq    mm0, [bx]
    paddw   mm0, [si]        ; four 16-bit lanes at once
    movq    [di], mm0
    add     bx, 8
    add     si, 8
    add     di, 8
    loop    Top
    emms
    ...                      ; scalar tail, then ret
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
A loop carrying a bounds check, or any other branch in its body, is not
matched, so a per-element trap still fires in element order. Under
`$ERROR OVERFLOW` an add or subtract loop of at least 32 elements is put behind a
checked kernel instead ([O0308](O0308-speculative-overflow-elimination.md)):
it scans for an overflow first and computes packed only if there is none,
otherwise the original loop runs and raises where it always did. Loops with
fewer than 8 trips (or fewer than one vector) stay scalar. The MMX kernel
executes `EMMS` before it returns, so no float code can follow a live MMX
state.

## Limits

Reductions, `a(i) OP scalar`, non-2-byte elements and variable trip counts are
[O0074](O0074-wider-vectorization.md). Only the MMX path executes under DOSBox;
the XMM/YMM/ZMM encodings are verified by assembler unit tests against
hand-computed VEX/EVEX opcodes.
