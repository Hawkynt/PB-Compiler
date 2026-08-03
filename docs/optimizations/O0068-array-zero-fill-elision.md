# O0068 — Array zero-fill elision

| | |
|---|---|
| **Status** | ⬜ Planned (needs the loop-fill pattern proof from [O0016](O0016-value-fact-analysis.md)) |
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

## Planned

The allocation reserves the storage without the fill, because the loop that
follows writes every element before anything reads one.

## What it needs

- A **coverage proof**: the fill loop's counter range must equal the array's
  bounds, its store must target every element exactly once, and it must
  dominate every read of the array. That is the same interval reasoning
  [O0016](O0016-value-fact-analysis.md) already does for bounds checks, applied
  to a different question.
- Safety rails mirroring [O0019](O0019-zero-elision.md): arrays of dynamic
  strings or types embedding string handles never qualify (a non-zero handle
  would be treated as a live allocation), and an `ON ERROR` handler that can
  re-enter before the fill completes blocks the proof.
