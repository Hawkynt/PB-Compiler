# O0229 — Array copy loop → `REP MOVSW`

| | |
|---|---|
| **Status** | ✅ Implemented (distinct 16-bit arrays) |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Optimize.cs` — `TryEmitForIdiom` |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` |
| **Split from** | [O0020](O0020-idiom-replacement.md) |

## What it is

`FOR i = lo TO hi : dst(i) = src(i) : NEXT` over two **distinct** 16-bit arrays
is a block move, including the near/far segment dance, with the counter's end
value preserved.

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
