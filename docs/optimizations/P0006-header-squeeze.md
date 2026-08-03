# P0006 — Header and padding squeeze

| | |
|---|---|
| **Status** | ✅ Implemented (minimal header, no inter-section padding, literal dedup, code folding) |
| **Stage** | Image writer |
| **Source** | `Emit/MzExeWriter.cs`, `Asm/Assembler.TailMerge.cs` |
| **Gate** | `--optimize`; code folding additionally needs `$OPTIMIZE SIZE` |
| **Related** | [O0011](O0011-literal-overlap-pooling.md), [O0040](O0040-identical-code-folding.md), [P0003](P0003-bss.md) |

## What it is

The last bytes, from the parts of the file that are not code:

- a **minimal MZ header** — 0x20 bytes, paragraph-aligned, instead of the
  padded-out classic layout;
- **no padding** between trimmed sections;
- **literal deduplication and overlap packing**
  ([O0011](O0011-literal-overlap-pooling.md));
- **cross-procedure tail merging** of identical code sequences
  ([O0040](O0040-identical-code-folding.md)).

## Sample

```basic
$OPTIMIZE SIZE
PRINT "Hello, World!"
PRINT "World!"
```

## Without the optimizer

```
MZ header      :  32 bytes, then padding to the load module boundary
Literals       :  13 + 6 = 19 bytes
Section gaps   :  padding between each emitted region
```

## With the optimizer

```
MZ header      :  32 bytes, paragraph-aligned, no slack
Literals       :  13 bytes ("World!" is a slice of "Hello, World!")
Section gaps   :  none
```

## Equivalent BASIC

Unchanged.

## Why it is safe

Every part of this is a layout decision: the header fields still describe the
image correctly, sections are still paragraph-aligned where the loader requires
it, and the literal/code sharing is governed by the soundness arguments on
[O0011](O0011-literal-overlap-pooling.md) (the pool is provably read-only) and
[O0040](O0040-identical-code-folding.md) (byte- and fixup-identical regions
only).
