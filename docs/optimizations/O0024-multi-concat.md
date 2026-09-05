# O0024 — Multi-concat single-allocation builder

| | |
|---|---|
| **Status** | ✅ Implemented (chains/trees of three or more safe operands) |
| **Stage** | Emitter + string runtime |
| **Source** | `CodeGen` — `FlattenStringConcat`, `EmitMultiConcat`; runtime `rt_strcatn`, `rt_catlist`, `rt_strcopyinto` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF94/95/107/108.BAS` (rerouted through the single-alloc path) |
| **IR** | ✅ `Ir/Passes/StringConcatChain.cs` — registered as `strchain` in `IrPassManager.Standard()`. Pairwise lowering of `a$ + b$ + c$ + d$` allocates a result per node and copies the growing prefix into each, O(n²) bytes moved for O(n) of output; `rt_str_concat_n` sums the lengths, reserves once and copies each operand in once. The flattening descends into an operand only when that operand is a concatenation whose ONLY reader is this one - a shared intermediate is a value the program uses twice, and a chain that consumed it would leave the other reader holding a freed handle |
| **Related** | [O0009](O0009-string-temp-economy.md), [R0003](R0003-string-engine.md) |

## What it is

A chain or tree of **three or more** string concatenations builds with a
**single** heap allocation and one byte-copy per operand, instead of the
pairwise `StrCat` chain's N−1 allocations and O(n²) recopying.

`FlattenStringConcat` collapses the maximal `&`/`+` tree over string operands
into its ordered list of leaves (both sides recursively, so right-nested and
mixed trees flatten too). `EmitMultiConcat` evaluates every leaf **strictly
left-to-right**, stages the handles into `rt_catlist`, and `rt_strcatn` then:

1. sums every operand's length in one pass;
2. calls `StrAlloc` **once**;
3. copies each operand's bytes in order;
4. frees every operand handle — consuming them exactly as the `StrCat` chain
   would.

## Sample

```basic
DIM a$, b$, c$, d$, s$
a$ = "alpha" : b$ = "beta" : c$ = "gamma" : d$ = "delta"
s$ = a$ + b$ + c$ + d$
PRINT s$
```

## Without the optimizer

Left-associative pairing: three allocations, and the growing prefix is copied
again at every node.

```asm
    call    StrCat           ; alloc #1: a$+b$          (9 bytes copied)
    call    StrCat           ; alloc #2: (a$+b$)+c$     (14 bytes copied)
    call    StrCat           ; alloc #3: (…)+d$         (19 bytes copied)
```

## With the optimizer

```asm
    ; evaluate a$, b$, c$, d$ left to right, stage handles in rt_catlist
    mov     cx, 0004h
    call    rt_strcatn       ; one StrAlloc, 19 bytes copied once, 4 frees
```

## Equivalent BASIC

```basic
DIM s$
s$ = SPACE$(LEN(a$) + LEN(b$) + LEN(c$) + LEN(d$))
MID$(s$, 1) = a$ : MID$(s$, 6) = b$ : ...
```

— i.e. exactly what a hand-written builder does, without the source noise.

## Why it is safe

Because all operands are staged up front and only then concatenated, every leaf
must yield a **fresh, independent, freeable handle** that a later operand's
evaluation cannot invalidate. A literal or a plain string variable does; a
function or intrinsic call returns a **shared, volatile** result buffer the next
call reuses, and an array element / member / pointer deref *borrows* live
storage. So `FlattenStringConcat` requires every leaf to satisfy
`IsReorderableStringExpr`, and a chain containing any other operand falls back
to the pairwise path, which consumes each operand immediately after evaluating
it.

`rt_strcopyinto` re-reads the descriptor for each copy, so a heap compaction
during the allocation is harmless — operands are relocated with their handles
intact. Empty operands (handle 0) read length 0, copy nothing and free nothing.

## Limits

Two-operand concatenation is already a single `StrCat` allocation (minimal), and
barrier operands are excluded for soundness, not economy — staging
`f$() & g$() & h$()` would alias and read `"hhh"`.
