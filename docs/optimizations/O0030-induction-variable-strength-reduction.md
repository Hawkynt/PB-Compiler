# O0030 — Induction-variable strength reduction

| | |
|---|---|
| **Status** | ✅ Implemented (near array element addresses affine in a constant-step counter, read and store) |
| **Stage** | IR (native pipeline, after the standard middle end) |
| **Source** | `Ir/Passes/AddressOffsetNarrowing.cs`, `Ir/Passes/AddressInduction.cs` (run from `IrMiddleEndPipeline.RunNativeModule`) |
| **Gate** | `--optimize`, constant `STEP`, near (non-far) element addresses |
| **Verified by** | `tests/diff/DIFF64.BAS` (read), `DIFF65.BAS` (store), `DIFF76.BAS` (LONG), scenario `AccumulateOverArrayIsHandQuality` |
| **Related** | [O0004](O0004-strength-reduction.md), [O0005](O0005-register-residency.md), [O0036](O0036-constant-subscript-folding.md) |

## What it is

A loop that walks an array by its counter does not need to recompute
`base + (i − lbound) * elementSize` every iteration: the address is itself an
induction variable. `AddressInduction` rewrites an element address
`gep base, scale*i + offset` with a loop-invariant base into a pointer carried
through a header phi and advanced by `scale * step` in the latch, so the hot
path becomes a plain load or store with no multiply and no address arithmetic.
`AddressOffsetNarrowing` first restates every offset at the pointer width,
where the arithmetic is modular, so no trip count or overflow proof is needed.

Covered shapes:

- element **reads** and **stores** alike, of any element size — the pass
  rewrites the address, not the access;
- offsets that also hold loop-invariant values — a row's start in a
  two-dimensional subscript, or a non-constant first counter value — which are
  computed once in the preheader;
- several subscripts: one pointer is carried per base, counter, scale and
  invariant part, and subscripts that differ by a constant (`a(i)` and
  `a(i + 3)`) share it through a displacement folded into the addressing mode;
- the **accumulate** loop `acc = acc OP a(i)` — the commonest loop there is —
  where the register allocator keeps the pointer, counter and accumulator in
  registers ([O0005](O0005-register-residency.md)).

## Sample

```basic
$OPTIMIZE SPEED
DIM a%(0 TO 999), i%, s%
FOR i% = 0 TO 999
  s% = s% + a%(i%)
NEXT
```

## Without the optimizer

Ten instructions per iteration, including a multiply that is also an 80186
instruction:

```asm
Top:
    mov     ax, [i]
    cmp     ax, 03E7h
    jg      Done
    mov     ax, [i]
    imul    ax, ax, 2        ; scale the subscript
    mov     bx, ax
    mov     ax, [a+bx]
    push    ax
    mov     ax, [s]
    pop     bx
    add     ax, bx
    mov     [s], ax
    inc     word ptr [i]
    jmp     Top
Done:
```

## With the optimizer

Six instructions, no memory traffic except the array element itself:

```asm
    mov     si, 0000h        ; counter
    lea     bx, [a]          ; element pointer
    xor     di, di           ; accumulator
Top:
    cmp     si, [limit]
    jg      Done
    add     di, [bx]         ; fused memory-operand ALU op
    add     bx, 2            ; step the pointer
    add     si, 1
    jmp     Top
Done:
```

This is what a person writes by hand.

## Equivalent BASIC

```basic
DIM p AS INTEGER PTR
p = VARPTR(a%(0))
FOR i% = 0 TO 999
  s% = s% + @p
  p = p +* 1            ' pb36 scaled pointer step
NEXT
```

## Why it is safe

The rewrite changes how an address is computed, not which address or when it
is used: every load and store stays where it was, in order, so an `$ERROR`
check still tests the subscript and fires on the element it always did. The
stepped pointer equals `base + scale*i + offset` on every iteration because
both are computed modulo the pointer width, which is how the machine forms an
address anyway. Only near pointers are stepped; a far element address is a
segment and an offset together. It runs after the loop vectorizer
([O0026](O0026-auto-vectorization.md)), which matches the counter-indexed form
this removes.

## Limits

Far (segment:offset) element addresses are not stepped, and the pass runs only
in the native pipeline; the C and LLVM emitters keep the subscript form.
