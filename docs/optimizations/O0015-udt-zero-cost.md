# O0015 — UDT zero-cost copy and compare

| | |
|---|---|
| **Status** | ✅ Implemented (wide block copy/compare, self-copy elision, self-compare folding) |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Optimize.cs` — `#region C1/R3 block-move widening`, `CodeGen/CodeGenerator.Expressions.cs` (`SameLValue`) |
| **Gate** | `--optimize`; DWORD-wide moves additionally need `$CPU 80386` |
| **Verified by** | `tests/diff/DIFF23.BAS` (odd/even TYPE sizes, copy + compare), `DIFF34.BAS` (self-copy/compare) |
| **Related** | [C0001](C0001-386-codegen.md), [R0003](R0003-string-engine.md), [O0059](O0059-scalar-replacement.md) |
| **Split into** | [O0214](O0214-udt-compare-widening.md), [O0215](O0215-udt-self-copy-elision.md), [O0216](O0216-udt-self-compare-fold.md), [O0242](O0242-movsd-block-copy.md) |

## What it is

**This page covers the whole-`TYPE` block copy.** A whole-UDT assignment runs
`REP MOVSW` (8086-safe) instead of a byte loop, with an odd tail byte-copied —
so an aggregate assignment costs one string operation rather than N.

Field access was already zero-cost: a direct constant-offset memory access.

The compare widening, the self-copy elision, the self-compare fold and the
DWORD-wide form each have their own entry (see *Split into* above).

## Sample

```basic
TYPE Point
  x AS INTEGER
  y AS INTEGER
  z AS INTEGER
  w AS INTEGER
END TYPE

DIM a AS Point, b AS Point
b = a
IF a = a THEN PRINT "same"
```

## Without the optimizer

```asm
    lea     si, [a]
    lea     di, [b]
    mov     cx, 0008h        ; 8 bytes
    rep     movsb            ; byte at a time
    lea     si, [a]          ; and a full memcmp against itself
    lea     di, [a]
    mov     cx, 0008h
    repe    cmpsb
    ...
```

## With the optimizer

```asm
    lea     si, [a]
    lea     di, [b]
    mov     cx, 0004h
    rep     movsw            ; 4 words (or 2 dwords under $CPU 80386)
    mov     ax, 0FFFFh       ; a = a folded to TRUE, no compare at all
```

## Equivalent BASIC

```basic
b.x = a.x : b.y = a.y : b.z = a.z : b.w = a.w
PRINT "same"
```

## Why it is safe

- Widening a block move changes only the transfer width, never the bytes moved;
  odd tails are handled explicitly.
- `SameLValue` is a **structural** lvalue-identity test, so only provably
  identical designators qualify for elision/folding.
- Types embedding dynamic-string handles are escape-blocked and keep the real
  copy/compare — the handle semantics (dup/free) must run.

## Limits

Scalar replacement (decomposing a non-escaping UDT into independent, register
allocatable field variables) and field-wise compare with early-out are
[O0059](O0059-scalar-replacement.md), which needs the register allocator.
