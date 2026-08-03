# O0008 — Peephole and zero idioms

| | |
|---|---|
| **Status** | ✅ Implemented (16- and 32-bit paths) |
| **Stage** | Emitter (immediate folding) + assembler (`RunPeephole`) |
| **Source** | `CodeGen/CodeGenerator.Expressions.cs`, `Asm/Assembler.Peephole.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF43.BAS` (add/sub), `DIFF44.BAS` (bitwise/compare), `DIFF45.BAS` (checked add/sub), `DIFF46.BAS` (`INC`/`DEC`), `DIFF47.BAS` (32-bit) |
| **Related** | [O0031](O0031-branch-fusion.md), [O0033](O0033-constant-store.md), [O0034](O0034-redundant-load-elimination.md), [O0035](O0035-jump-relaxation.md) |
| **Split into** | [O0202](O0202-int16-immediate-folding.md), [O0203](O0203-int32-immediate-folding.md), [O0204](O0204-inc-dec-idiom.md), [O0205](O0205-or-self-zero-test.md), [O0206](O0206-memory-incr-in-place.md) |

## What it is

**This page covers the zero idiom**: a constant zero at an expression position
is materialized with `XOR r,r` — two bytes instead of three — which is safe
there because the position is flag-dead by construction.

The other local rewrites each have their own entry (see *Split into* above):
16- and 32-bit immediate folding, `INC`/`DEC`, the `OR reg,reg` zero test, and
the in-place memory increment.

## Sample

```basic
DIM n%, m%
n% = 0
n% = n% + 1
m% = n% AND &H00FF
IF m% = 0 THEN PRINT "zero"
```

## Without the optimizer

```asm
    mov     ax, 0000h
    mov     [n], ax
    mov     ax, [n]
    push    ax
    mov     ax, 0001h
    mov     bx, ax
    pop     ax
    add     ax, bx
    mov     [n], ax
    mov     ax, [n]
    push    ax
    mov     ax, 00FFh
    mov     bx, ax
    pop     ax
    and     ax, bx
    mov     [m], ax
    mov     ax, [m]
    push    ax
    mov     ax, 0000h
    mov     bx, ax
    pop     ax
    cmp     ax, bx
    ...
```

## With the optimizer

```asm
    xor     ax, ax
    mov     [n], ax
    mov     ax, [n]
    inc     ax
    mov     [n], ax
    mov     ax, [n]
    and     ax, 00FFh
    mov     [m], ax
    mov     ax, [m]
    or      ax, ax
    jnz     NotZero          ; with O0031, the flags drive the branch directly
```

## Equivalent BASIC

The program is unchanged — these are encoding choices, not semantic rewrites.

## Why it is safe

- The immediate is taken modulo 2¹⁶ (the same low word the register path would
  have coerced into BX), so the arithmetic result is identical.
- `INC`/`DEC` set OF exactly like `ADD`/`SUB` by one, so the `$ERROR OVERFLOW`
  `JNO` guard is preserved; they leave CF alone, which none of these paths read.
- `OR AX,AX` clears OF, which is harmless: with OF = 0 both the signed and the
  unsigned conditions reduce to SF/CF tests.
- `XOR r,r` is only used at expression positions where the flags are dead.

## Limits

The assembler-level peephole and the [instruction
scheduler](O0038-instruction-scheduling.md) rewrite by recorded position and are
therefore **mutually exclusive**: `pb36` + `$OPTIMIZE SPEED` gets the scheduler,
every other optimized standalone build keeps the peephole.
