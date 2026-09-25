# O0204 — `INC`/`DEC` for ±1

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | x86 back end (peephole) |
| **Source** | `Backend/SuperoptimizedPeepholes.cs` (register `ADD`/`SUB` ±1); `Backend/Peephole.cs` — `FoldReadModifyWrites`, `UnitStep` (memory cell); `Backend/PostRegisterAllocationPeepholes.cs` — `TryStepInPlace` (`LEA r,[r±1]`) |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF46.BAS` |
| **Split from** | [O0008](O0008-peephole-zero-idiom.md) |

## What it is

An add or subtract of exactly 1 becomes `INC` or `DEC`: one byte instead of
three. The register form comes from `SuperoptimizedPeepholes`, whose catalog is
proved over all 65,536 word inputs at startup; `Peephole` does the same for a
read-modify-write of a memory cell, and the post-allocation peephole for a
pointer stepped by one with `LEA`.

## Sample

```basic
DIM n%
n% = n% + 1
```

## Without / with

```asm
    mov     ax, [n]          ; without (already immediate-folded)
    add     ax, 0001h        ; 3 bytes
    mov     [n], ax

    mov     ax, [n]          ; with
    inc     ax               ; 1 byte
    mov     [n], ax
```

## Why it is safe

The value written is identical, and `INC`/`DEC` differ from `ADD`/`SUB` only in
the flags (they leave **CF** alone). Every rewrite therefore requires the flags
to be dead after the instruction (`MachineFlags.DeadAfter`), so nothing
downstream can observe the difference. The `$ERROR OVERFLOW` trap does not read
OF on this path — the IR lowering tests the operands' and result's signs
explicitly — so checked arithmetic is covered by the same rule.

## See also

For a variable that is not already in a register,
[O0206](O0206-memory-incr-in-place.md) increments the cell directly.
