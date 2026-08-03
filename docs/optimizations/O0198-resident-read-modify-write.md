# O0198 — Resident read-modify-write

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Optimize.cs` — `TryEmitResidentReadModifyWrite` |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` |
| **Verified by** | scenario `AccumulateOverArrayIsHandQuality` |
| **Split from** | [O0005](O0005-register-residency.md) |

## What it is

Even with an accumulator resident in DI, the naive emission of `acc = acc + a(i)`
still routes through the accumulator register: load DI into AX, add, copy back.
Targeting the resident register **directly** removes both moves.

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

The rewrite applies only when the target of the assignment *is* the resident
register's variable and the right-hand side is that variable combined with one
memory operand — the same value, computed in place.
