# O0068 — Array zero-fill elision

| | |
|---|---|
| **Status** | 🟡 Partial — a dynamic rank-1 array a directly-following FOR fills in full allocates without the zero-fill; static arrays and non-adjacent / partial fills remain |
| **Stage** | Emitter (array allocation) |
| **Related** | [O0019](O0019-zero-elision.md), [O0016](O0016-value-fact-analysis.md), [O0020](O0020-idiom-replacement.md) |

## The idea

[O0019](O0019-zero-elision.md) drops the per-invocation *frame* zeroing when
every local is provably assigned before use. The same argument applies to
arrays: PB zero-fills an array at `DIM`/`REDIM`/`ArrAlloc` time, and when an
initialization loop provably **dominates every read** and covers every element,
that fill is unobservable.

For a large array this is not a small saving — a `DIM a%(0 TO 32000)` costs a
64 KB `REP STOSW` before the program's own fill loop writes the same memory.

## Applies to

```basic
DIM a%(0 TO 9999), i%
FOR i% = 0 TO 9999
  a%(i%) = i%
NEXT
PRINT a%(500)
```

## Today

```asm
    ; ArrAlloc
    push    ds
    pop     es
    lea     di, [a]
    mov     cx, 2710h
    xor     ax, ax
    rep     stosw            ; 10 000 words zeroed...
    ...                      ; ...and then immediately overwritten
```

## Now

```asm
    ; DIM a(1 TO n) covered by FOR i = 1 TO n : a(i) = i*i
    ...                          ; bounds + byte count as usual
    call    rt_arr_alloc_nz      ; bump-allocate, NO rep stosb
```

`PrepareArrayFill` (per body, gated on no error handler) marks a `DIM`
immediately followed by a covering `FOR`; `EmitDim` then calls `rt_arr_alloc_nz`
— the same bump allocator minus the `REP STOSB` — instead of `rt_arr_alloc`. For
a `DIM a(0 TO 32000)` that skips a 64 KB fill the program's own loop is about to
overwrite.

### The coverage proof (`IsCoveredArrayFill`)

Conservative and syntactic, so it can never keep a live zero:

- the array is a single **conventional dynamic rank-1** non-string array (a type
  embedding a string handle never qualifies — a garbage handle would corrupt the
  string heap);
- the very next statement is `FOR i = <lower> TO <upper>` matching the array's
  **explicit** bounds with step 1 (so element *i* is written on pass *i*, every
  element exactly once);
- the body is the lone assignment `a(i) = expr` subscripted by the counter;
- `expr` calls nothing and reads no element of `a` itself (`IsSafeFillValue`) —
  so it cannot observe a not-yet-written element; a read of a **different** array
  (`a(i) = b(i)`, distinct storage, no alias) is allowed, so array copies qualify;
- **no error handler** in the body — a trapping fill could otherwise re-enter a
  handler that reads the array before the loop finishes.

Verified byte-identical against the genuine oracle (fill then sum every element),
self-differential (optimized == the golden-faithful build), and a regression test
that the covered fill emits the no-zero allocator while a fill that *reads* the
array keeps the zero-filling one. The no-zero routine lives in its own trimmer
section, so under `$OPTIMIZE OFF` nothing references it and the faithful image is
byte-for-byte unchanged.

## Still planned

- **Static arrays** (constant bounds) — zeroed via the frame/data fill, a
  different mechanism ([O0019](O0019-zero-elision.md)'s territory), so the doc's
  `DIM a%(0 TO 9999)` constant-bound example is not yet covered; only dynamic
  (`$DYNAMIC` / runtime-bound / `REDIM`-class) arrays are.
- **`REDIM`**, multi-statement fills, and from-both-ends or strided coverage —
  each a widening of the proof.

Native-only. On the IR back ends the array is a heap buffer the C/LLVM optimizer
already sees fully overwritten, so it elides the fill (or the `calloc`→`malloc`
rewrite) itself.
