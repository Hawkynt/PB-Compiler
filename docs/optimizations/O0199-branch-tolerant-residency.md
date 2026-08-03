# O0199 — Residency across a conditional

| | |
|---|---|
| **Status** | ✅ Implemented |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.Optimize.cs` — the SI/DI-clean proof |
| **Gate** | `--optimize` + `$OPTIMIZE SPEED` |
| **Verified by** | `tests/diff/DIFF91.BAS` |
| **Split from** | [O0005](O0005-register-residency.md) |

## What it is

The clean-body proof accepts a **conditional** (`IF`/`ELSEIF`/`ELSE`) whose test
is SI-clean and whose arms are themselves SI-clean. A branch touches no general
register, so the counter in SI and any DI resident survive it.

Before this, any branching in the body disabled residency altogether — which
excluded the very common conditional-accumulate shape.

## Sample

```basic
$OPTIMIZE SPEED
DIM i%, s%, a%(0 TO 99)
FOR i% = 0 TO 99
  IF a%(i%) > 0 THEN s% = s% + a%(i%)
NEXT
```

## With the optimizer

```asm
    mov     si, 0000h        ; counter
    xor     di, di           ; accumulator survives the branch
Top:
    ...
    cmp     ax, 0000h
    jle     Skip
    add     di, ax
Skip:
    inc     si
    jmp     Top
```

## Why it is safe

Every arm is checked independently; one arm that clobbers the register (a call, a
string operation, inline asm) disqualifies the whole body. The residency flush on
exit is unchanged.
