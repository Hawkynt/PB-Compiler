# O0229 — Array copy loop → `REP MOVSW`

| | |
|---|---|
| **Status** | ⬜ Not implemented on the IR path |
| **Stage** | IR middle end (planned) |
| **Source** | None. The nearest current code is `Ir/Passes/LibraryCallRecognition.cs` ([O0330](O0330-library-call-recognition.md)), which handles byte-element copy loops only |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` |
| **Split from** | [O0020](O0020-idiom-replacement.md) |

## What it is

`FOR i = lo TO hi : dst(i) = src(i) : NEXT` over two **distinct** 16-bit arrays
is a block move, including the near/far segment dance, with the counter's end
value preserved.

Not implemented on the IR path; the syntax-level version was retired with the
direct emitter. `LibraryCallRecognition` turns a counted copy loop into an
`llvm.memcpy` (`rt_memcpy`, which runs `REP MOVSB`, or `REP MOVSD` plus a byte
tail on an optimized 386+ target) only when each element is one byte and the
index steps the byte offset by one, so a 16-bit array copy stays a loop.

## Sample

```basic
$OPTIMIZE SPEED
DIM src%(0 TO 99), dst%(0 TO 99), i%
FOR i% = 0 TO 99
  dst%(i%) = src%(i%)
NEXT
```

## Without / with

```asm
    ; without: 100 iterations of index, scale, load, store

    push    ds               ; with
    pop     es
    lea     si, [src]
    lea     di, [dst]
    mov     cx, 0064h
    rep     movsw
    mov     word ptr [i], 0064h
```

## Why it is safe

The arrays must be **distinct** — a self-copy or an overlapping range would
depend on the element order that `REP MOVSW` fixes — and the index expressions
must be the bare counter, so the mapping is element-for-element. Under `$CPU
80386` the move widens to `REP MOVSD` with a byte tail.
