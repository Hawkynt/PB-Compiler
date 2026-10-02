# O0214 — Whole-UDT compare widening

| | |
|---|---|
| **Status** | ⬜ Not implemented on the IR path |
| **Stage** | — |
| **Source** | None. A whole-UDT compare lowers to `rt_mem_compare` (`Ir/IrLowering.cs` — `LowerUdtComparison`), which is the runtime's `REPE CMPSB` loop (`Runtime/DosRuntime.Memory.cs` — `rt_memcmp`) |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF23.BAS` |
| **Split from** | [O0015](O0015-udt-zero-cost.md) (which is now the block copy) |

## What it is

The PowerBASIC 3.1 whole-value `=`/`<>` comparison of two `TYPE` values is a
memory compare. For an even byte size it runs `REPE CMPSW` — half the iterations
of `REPE CMPSB`.

Not implemented on the IR path; the emitter-level version was retired with the
direct emitter. The related `Ir/Passes/AggregateBlockScalarization.cs` replaces
an equality-only `rt_mem_compare` with per-field compares when the surrounding
typed accesses prove the record's complete layout; otherwise the byte compare
runs.

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
