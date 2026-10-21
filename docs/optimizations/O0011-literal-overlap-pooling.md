# O0011 — Literal overlap pooling

| | |
|---|---|
| **Status** | 🟡 Partial — exact dedup and proven containment; partial prefix/suffix overlap remains open |
| **Stage** | Final shared IR literal layout, after string folding |
| **Source** | `Ir/Passes/LiteralPooling.cs`, owned by `IrMiddleEndPipeline` |
| **Gate** | `--optimize` |
| **Related** | [O0009](O0009-string-temp-economy.md), [P0006](P0006-header-squeeze.md) |

## What it is

Identical string literals are interned during lowering, even without optimization.
The optimized middle end additionally visits eligible literals longest first and
replaces contained globals with a host pointer plus a byte offset. Explicit lengths
remain unchanged, including when the smaller literal starts at offset zero.
`"World!"` is a slice of `"Hello, World!"`, and so is `"lo, W"`. Unoptimized,
distinct literals retain separate byte arrays.

Both production pipeline runners apply this final transform, so C and LLVM receive
the same shared bytes as native targets. The BASIC writer reconstructs the exact
slice using its offset and length. The x86-16 selector accepts constant byte offsets
for literal-address runtime arguments. This is an IR optimization, not a request
for the host C compiler to merge arrays. Native image layout still packs literals
created outside the IR, such as runtime-owned strings.

Literals that merely overlap — the suffix of one being the prefix of another —
are not merged by this pass. General constant-data merging (O0285) remains separate
and deliberately excludes string globals.

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

Both host and contained globals must have only proven read-only uses: declaration
calls to `rt_print_str`, `rt_fprint_str`, `rt_str_const`, or `rt_str_append_lit`,
with an explicit constant length within the original byte vector. Those routines
read or copy the supplied bytes; they do not expose the source address. A defined
function with the same name is not assumed to satisfy that contract.

Stores, pointer comparisons, unknown calls, dynamic or invalid lengths, other
pointer uses, and empty payloads are not pooled. Opaque inline assembly disables
the transform for the module. Bytes are compared directly, including embedded NULs
and high-bit bytes; no encoding conversion or terminator is involved. More general
read-only/escape proofs, including calls to defined portable-runtime bodies, remain
outside this conservative slice.

`LiteralPoolingTests` checks production routing for every target, native COM/MZ
execution, BASIC round-tripping, bounded consumer slices, binary bytes, unsafe uses,
idempotence, and re-interning after removal. `CBackendTests` builds and executes the
generated C: `"Hallo"` and `"World"` share `"Hallo World"`, leaving 15 rather than
25 stored bytes when `"Test"` is also printed.
