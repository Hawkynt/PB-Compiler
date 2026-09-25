# O0020 — Algorithmic idiom replacement

| | |
|---|---|
| **Status** | 🟡 Partial — inline `SWAP` of scalar cells is implemented; an empty loop is deleted only when nothing reads its counter afterwards (fill, series and copy are on their split pages) |
| **Stage** | IR lowering and middle end (loop passes); x86 back end (peephole) for `SWAP` |
| **Source** | `Ir/Passes/DeadLoopElimination.cs`, `Ir/Passes/RecurrenceClosedForm.cs` (empty loop); `Ir/IrLowering.cs` — `LowerSwap`, `Backend/Peephole.cs` — `FoldSwaps` (`SWAP`) |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` |
| **Related** | [O0007](O0007-loop-unrolling.md), [O0025](O0025-pure-function-folding.md), [O0073](O0073-algorithmic-idiom-catalog.md) |
| **Split into** | [O0227](O0227-constant-fill-stosw.md), [O0228](O0228-series-folding.md), [O0229](O0229-copy-loop-movsw.md) |

## What it is

Instead of optimizing a loop instruction by instruction, the compiler recognizes
what the **whole loop computes** and substitutes a better algorithm — but only
where the result is provably bit-identical.

**This page covers the empty loop**: a counted loop whose body computes nothing
anyone reads. `DeadLoopElimination` deletes it under `$OPTIMIZE SPEED` when its
trip count is a known finite number, every body instruction is discardable, and
nothing the loop defines — the counter included — is read after it. An
accumulator that only adds a constant is first replaced after the loop by its
closed form `start + step * trips` (`RecurrenceClosedForm`), which is what
empties such a loop. The counter's own exit value is not computed, so a loop
whose counter is read afterwards stays. The other recognized shapes — constant
fill, arithmetic series, array copy — each have their own entry (see *Split
into* above).

**Inline `SWAP`** is a non-loop idiom in the same spirit: `LowerSwap` lowers
`SWAP a, b` of two scalars — INTEGER/LONG/BYTE, or a dynamic string's handle —
as two loads and two crossed stores, never the runtime `rt_swap` byte loop.
The commonest use is a sort inner loop. When the values are not otherwise
needed in registers, the back end's `Peephole.FoldSwaps` rewrites the four moves
as `mov r,[a]; xchg r,[b]; mov [a],r`. A string's handle changes owner exactly
once, so nothing is copied or freed. A whole UDT is swapped by three block
copies through a frame temporary. Verified by an `absent-call rt_swap` byte
assertion and `Emit_GivenScalarSwap_WhenPb36_ThenInlineXchgNotRuntimeCall`.

## Sample

```basic
$OPTIMIZE SPEED
DIM i%, s%, a%(0 TO 99)
FOR i% = 1 TO 1000 : NEXT              ' empty
FOR i% = 0 TO 99 : a%(i%) = 7 : NEXT   ' constant fill
FOR i% = 1 TO 100 : s% = s% + i% : NEXT ' arithmetic series
PRINT i%; s%
```

## Without the optimizer

Three real loops — 1 000 + 100 + 100 iterations of compare, body, increment and
back-edge.

## With the optimizer

```asm
    mov     word ptr [i], 03E9h    ; 1001: the empty loop IS its end value
    push    ds                     ; constant fill
    pop     es
    lea     di, [a]
    mov     cx, 0064h
    mov     ax, 0007h
    rep     stosw
    mov     word ptr [i], 0064h
    mov     ax, [s]                ; the series total, added once
    add     ax, 13BAh              ; 5050
    mov     [s], ax
    mov     word ptr [i], 0065h
```

(This shows the full recognition. The empty loop here is kept today, because
the `PRINT i%` reads its counter after the loop.)

## Equivalent BASIC

```basic
DIM i%, s%, a%(0 TO 99)
i% = 1001
' a%() filled with 7 by a block store
i% = 101
s% = s% + 5050
PRINT i%; s%
```

## Why it is safe

- A loop is deleted only with a known finite trip count, so a loop that never
  ends is never replaced by one that does.
- Every body instruction must be discardable under the central effect
  contract: a store, a possible trap (an `$ERROR` check, a division),
  a call with unknown effects or inline assembly keeps the loop.
- `$OPTIMIZE SPEED` gating is not a performance preference but a correctness
  courtesy: DOS-era code uses empty loops as **delay loops**, and under SPEED
  such a loop goes too; `SLEEP` and `DELAY` are the way to spell a wait. Under
  `$OPTIMIZE SIZE` the loop stays.
- The closed form is limited to INTEGER accumulators: two's-complement addition
  wraps the same whether it is repeated or multiplied, and float rounding does
  not.

## Limits

MIN/MAX scans, bubble-sort shapes lowering to `ARRAY SORT`, and further whole
algorithm recognitions are [O0073](O0073-algorithmic-idiom-catalog.md).
