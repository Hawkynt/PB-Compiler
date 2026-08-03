# O0214 — Whole-UDT compare widening

| | |
|---|---|
| **Status** | ✅ Implemented (even sizes) |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Optimize.cs` — block-move/compare widening |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF23.BAS` |
| **Split from** | [O0015](O0015-udt-zero-cost.md) (which is now the block copy) |

## What it is

The PowerBASIC 3.1 whole-value `=`/`<>` comparison of two `TYPE` values is a
memory compare. For an even byte size it runs `REPE CMPSW` — half the iterations
of `REPE CMPSB`.

## Sample

```basic
TYPE Point
  x AS INTEGER
  y AS INTEGER
END TYPE
DIM a AS Point, b AS Point
IF a = b THEN PRINT "same"
```

## Without / with

```asm
    mov     cx, 0004h        ; without: 4 byte comparisons
    repe    cmpsb

    mov     cx, 0002h        ; with: 2 word comparisons
    repe    cmpsw
```

## Why it is safe

Comparing the same bytes in wider units yields the same equality answer; only
equality/inequality is exposed by the language, so the *ordering* difference
between byte-wise and word-wise comparison is unobservable. Odd sizes and types
embedding dynamic-string handles keep the byte path.
