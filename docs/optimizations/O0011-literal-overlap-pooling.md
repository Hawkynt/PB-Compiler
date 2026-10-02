# O0011 — Literal overlap pooling

| | |
|---|---|
| **Status** | 🟡 Partial — exact dedup and containment are implemented; prefix/suffix overlap is not implemented on the IR path |
| **Stage** | String-literal pool layout (image data) |
| **Source** | `CodeGen/CodeGenerator.cs` — `EmitStringLiterals` |
| **Gate** | `--optimize` |
| **Related** | [O0009](O0009-string-temp-economy.md), [P0006](P0006-header-squeeze.md) |

## What it is

String literals are stored once in a pool and addressed by offset + length.
Beyond exact deduplication, `EmitStringLiterals` lays the pool out longest first
and, when optimizing, gives a literal whose bytes occur inside an already placed
one no bytes of its own: its label marks the place inside the longer literal.
`"World!"` is a slice of `"Hello, World!"`, and so is `"lo, W"`. Unoptimized,
each literal keeps its own bytes, as the vintage compilers lay them out.

Literals that merely overlap — the suffix of one being the prefix of another —
are not merged. Not implemented on the IR path; the syntax-level version was
retired with the direct emitter.

Because every use site supplies its own length, any slice of the pool is a valid
literal address — no terminator is needed and no copy is made.

## Sample

```basic
PRINT "Hello, World!"
PRINT "World!"
PRINT "lo, W"
```

## Without the optimizer

Three independent pool entries — 13 + 6 + 5 = 24 bytes:

```
lit0:  db "Hello, World!"
lit1:  db "World!"
lit2:  db "lo, W"
```

## With the optimizer

One entry, 13 bytes; the other two are offsets into it:

```
lit0:  db "Hello, World!"
       ; lit1 = lit0+7, len 6
       ; lit2 = lit0+3, len 5
```

```asm
    mov     dx, offset lit0
    mov     cx, 000Dh
    call    rt_print_str
    mov     dx, offset lit0 + 7
    mov     cx, 0006h
    call    rt_print_str
    mov     dx, offset lit0 + 3
    mov     cx, 0005h
    call    rt_print_str
```

## Equivalent BASIC

```basic
DIM base$
base$ = "Hello, World!"
PRINT base$
PRINT MID$(base$, 8, 6)
PRINT MID$(base$, 4, 5)
```

…except that no copy is made and `base$` occupies no heap.

## Why it is safe

Sound only while the pool stays **read-only**. Every consumer of a literal
copies its bytes by address and length and never writes the pool, so two
labels into one run of bytes cannot be told apart from two separate runs. The
layout reads nothing but the pool itself.
