# O0011 — Literal overlap pooling

| | |
|---|---|
| **Status** | ✅ Implemented (exact dedup + containment + prefix/suffix overlap) |
| **Stage** | Emitter, string-literal pool construction |
| **Source** | `CodeGen/CodeGenerator.Optimize.cs` — `#region O11` |
| **Gate** | `--optimize` |
| **Related** | [O0009](O0009-string-temp-economy.md), [P0006](P0006-header-squeeze.md) |

## What it is

String literals are stored once in a pool and addressed by offset + length.
Beyond exact deduplication, the packer places literals so that **contained** and
**overlapping** ones share the same bytes: `"World!"` is a slice of
`"Hello, World!"`, and so is `"lo, W"`.

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

Sound only while the pool stays provably **read-only**, which generated code
guarantees: literals are read by `StrMem` copies and `PrintStr` and are never
written. Escape analysis disables packing for a literal whose storage could
leak — `VARPTR`/`STRPTR` over a literal-backed value, inline asm referencing a
literal label, or a BYREF/external call that could write through the reference.
An escaping literal falls back to a private copy.
