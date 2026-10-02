# O0216 — UDT self-compare folding

| | |
|---|---|
| **Status** | ⬜ Not implemented on the IR path |
| **Stage** | — |
| **Source** | None. A whole-UDT compare lowers to `rt_mem_compare` (`Ir/IrLowering.cs` — `LowerUdtComparison`), which nothing folds when both addresses are the same |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF34.BAS` |
| **Split from** | [O0015](O0015-udt-zero-cost.md) |

## What it is

`rec = rec` as an **expression** folds to its constant truth: `-1` for `=`, `0`
for `<>`. A memory comparison of identical bytes is always equal.

Not implemented on the IR path; the syntax-level version (`SameLValue`) was
retired with the direct emitter, and `r = r` runs the runtime byte compare
unless `Ir/Passes/AggregateBlockScalarization.cs` has first split it into
per-field compares.

## Sample

```basic
DIM r AS Rec
IF r = r THEN PRINT "always"
```

## Without / with

```asm
    lea     si, [r]          ; without: a full memcmp against itself
    lea     di, [r]
    mov     cx, 0003h
    repe    cmpsw
    ...

    mov     ax, 0FFFFh       ; with
```

## Why it is safe

Unlike a floating-point self-comparison, this is **NaN-immune**: the UDT compare
is a byte comparison, not a numeric one, so identical storage always compares
equal regardless of the bit patterns it holds. Structural lvalue identity
(`SameLValue`) is required, and string-bearing types are excluded.
