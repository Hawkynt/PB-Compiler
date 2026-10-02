# O0215 — UDT self-copy elision

| | |
|---|---|
| **Status** | ⬜ Not implemented on the IR path |
| **Stage** | — |
| **Source** | None. A whole-record assignment lowers to `llvm.memcpy` (`Ir/IrLowering.cs`), which nothing checks for identical source and destination |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF34.BAS` |
| **Split from** | [O0015](O0015-udt-zero-cost.md) |

## What it is

`rec = rec`, where both sides are the structurally identical non-string lvalue,
copies a block onto itself. It is a pure no-op and is not emitted at all.

The pattern is rare in hand-written source and routine after inlining and
specialization, where a copy's source and target become the same designator.

Not implemented on the IR path; the syntax-level version (`SameLValue`) was
retired with the direct emitter. `r = r` now copies the record onto itself
through `rt_memcpy`, or through the scalar load/store pairs
`MemoryRoutineSpecialization` expands a small copy into.

## Sample

```basic
TYPE Rec
  a AS INTEGER
  b AS LONG
END TYPE
DIM r AS Rec
r = r
```

## Without / with

```asm
    lea     si, [r]          ; without
    lea     di, [r]
    mov     cx, 0003h
    rep     movsw

    ; with: nothing
```

## Why it is safe

`SameLValue` is a **structural** identity test, so only provably identical
designators qualify — `a(i) = a(j)` never does. Types embedding dynamic-string
handles are excluded, because their assignment has ownership side effects
(dup/free) that a block copy does not.
