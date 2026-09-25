# O0198 — Resident read-modify-write

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | x86 back end (peephole + register allocation) |
| **Source** | `Backend/Peephole.cs` — `FoldMemorySources`; `Backend/CopyCoalescer.cs` (run by `Backend/LinearScanAllocator.cs`) |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` |
| **Verified by** | scenario `AccumulateOverArrayIsHandQuality`, `BackendResidencyTests` |
| **Split from** | [O0005](O0005-register-residency.md) |

## What it is

Even with an accumulator resident in a register, the naive selection of
`acc = acc + a(i)` still stages through a fresh register: the two-address `ADD`
starts with a copy of the accumulator, and the element is loaded into a register
of its own. Two back-end steps remove both. `Peephole.FoldMemorySources` turns
`MOV v,[n] / ADD d,v` into `ADD d,[n]`, and the copy coalescer merges the
accumulator's copy with its loop-carried value, so the add targets the resident
register **directly**.

This is the last gap between the generated accumulate loop and hand-written
assembly.

## Sample

```basic
$OPTIMIZE SPEED
DIM a%(0 TO 999), i%, s%
FOR i% = 0 TO 999
  s% = s% + a%(i%)
NEXT
```

## Without / with

```asm
    mov     ax, di           ; without
    add     ax, [bx]
    mov     di, ax

    add     di, [bx]         ; with
```

Combined with the SI counter, the BX element pointer
([O0030](O0030-induction-variable-strength-reduction.md)) and the fused memory
operand, the whole body is:

```asm
    cmp     si, [limit]
    jg      done
    add     di, [bx]
    add     bx, 2
    add     si, 1
    jmp     top
```

## Why it is safe

The memory fold applies only when the loaded value is defined and read by those
two instructions alone and nothing between them can change the cell or its
address. The coalescer merges a copy's two registers only when no definition of
either lands where the other is still live — the same value, computed in place.
