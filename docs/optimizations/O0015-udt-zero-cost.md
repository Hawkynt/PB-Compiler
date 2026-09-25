# O0015 — UDT zero-cost copy and compare

| | |
|---|---|
| **Status** | 🟡 Partial — small constant-size copies become word/dword moves and larger ones go through the runtime's copy (`REP MOVSD` on an optimized 386+ target); the `REP MOVSW` form is not implemented on the IR path |
| **Stage** | IR lowering + IR middle end + runtime |
| **Source** | `Ir/IrLowering.cs` (whole-record assignment as `llvm.memcpy`); `Ir/Passes/MemoryRoutineSpecialization.cs` — `TryMemcpy`; `Ir/Passes/AggregateBlockScalarization.cs`; `Runtime/DosRuntime.Memory.cs` — `rt_memcpy`; `Runtime/DosRuntime.Core.cs` — `EmitRepMovsbWidened` |
| **Gate** | `--optimize`; DWORD-wide moves additionally need `$CPU 80386` |
| **Verified by** | `tests/diff/DIFF23.BAS` (odd/even TYPE sizes, copy + compare), `DIFF34.BAS` (self-copy/compare) |
| **Related** | [C0001](C0001-386-codegen.md), [R0003](R0003-string-engine.md), [O0059](O0059-scalar-replacement.md) |
| **Split into** | [O0214](O0214-udt-compare-widening.md), [O0215](O0215-udt-self-copy-elision.md), [O0216](O0216-udt-self-compare-fold.md), [O0242](O0242-movsd-block-copy.md) |

## What it is

**This page covers the whole-`TYPE` block copy.** IR lowering spells a
whole-UDT assignment as an `llvm.memcpy` of the record's size. When optimizing,
`MemoryRoutineSpecialization` expands a constant-size copy into paired
load/store scalars — words on an 8086/286, dwords on a 386+, a byte for an odd
tail — as long as it needs no more stores than the target's budget
(`TargetCost.MaxStoresPerMemcpy`: 3 on an 8086, 4 on a 286/386, more on later
cores). A larger copy calls `rt_memcpy`, which is `REP MOVSB` on an 8086 and,
on an optimized 386+ target, `REP MOVSD` followed by a `REP MOVSB` tail.
`AggregateBlockScalarization` goes further where the record's field accesses
prove a complete byte partition, splitting the copy into those fields.

A `REP MOVSW` form for 8086 targets is not implemented on the IR path; the
syntax-level version was retired with the direct emitter. The listing below
shows that retired form.

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
- `llvm.memcpy` promises non-overlapping operands, so each scalar load can be
  paired directly with its store. Far pointers, volatile copies and
  non-constant sizes keep the call.
- `AggregateBlockScalarization` splits a copy only when the regions cover the
  whole record without gaps or overlap; padding, UNION type-punning, dynamic
  offsets and escapes keep the byte operation.

## Limits

Scalar replacement (decomposing a non-escaping UDT into independent, register
allocatable field variables) and field-wise compare with early-out are
[O0059](O0059-scalar-replacement.md), which needs the register allocator.
