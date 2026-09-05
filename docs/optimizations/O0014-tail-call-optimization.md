# O0014 — Tail-call optimization

| | |
|---|---|
| **Status** | ✅ Implemented (self-call and general cross-procedure `SUB` tail calls) |
| **Stage** | Emitter |
| **Source** | `CodeGen/CodeGenerator.cs`, `CodeGen/CodeGenerator.Procs.cs` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF29.BAS` (self-recursion + blocked twins), `DIFF87.BAS` (mutual recursion, differing argument counts, a deliberately non-tail call) |
| **IR** | ✅ `Ir/Passes/TailRecursion.cs` — the self-call half, registered as `tailrec` in `IrPassManager.Standard()`. A new entry block is pushed in front of the old one, which becomes a loop header; each parameter turns into a phi taking the original argument on the way in and the call's argument on the way round, and the call plus its return become a branch back. Not a size or speed optimization: without it a deep recursion overflows, which is why the routed path had to earn this one rather than inherit it (`BackendTailRecursionTests`) |
| **Related** | [O0006](O0006-inlining.md), [O0070](O0070-leaf-frame-elision.md) |
| **Split into** | [O0213](O0213-cross-procedure-tail-call.md) |

## What it is

**This page covers the self-call.** A self-call in tail position — the last
statement of the body, or of a trailing `IF`/`SELECT` arm chain — rewrites its
parameter slots in place and jumps back to the frame entry, re-zeroing locals
exactly as a fresh invocation would.

Recursion then runs in **constant stack space**: a 60 000-deep tail recursion
completes where the genuine compiler's default 2 KiB stack dies at about 170
frames.

The cross-procedure form is [O0213](O0213-cross-procedure-tail-call.md).

## Sample

```basic
SUB CountDown(BYVAL n%)
  IF n% = 0 THEN EXIT SUB
  PRINT n%
  CALL CountDown(n% - 1)      ' tail position
END SUB

CALL CountDown(60000)
```

## Without the optimizer

```asm
CountDown:
    push    bp
    mov     bp, sp
    ...
    mov     ax, [bp+6]
    dec     ax
    push    ax
    call    CountDown        ; a new frame per level -> stack overflow
    mov     sp, bp
    pop     bp
    ret     2
```

## With the optimizer

```asm
CountDown:
    push    bp
    mov     bp, sp
Entry:
    ...
    mov     ax, [bp+6]
    dec     ax
    mov     [bp+6], ax       ; rewrite the parameter slot in place
    jmp     Entry            ; no CALL, no new frame
```

## Equivalent BASIC

```basic
SUB CountDown(BYVAL n%)
  DO
    IF n% = 0 THEN EXIT SUB
    PRINT n%
    n% = n% - 1
  LOOP
END SUB
```

## Why it is safe

Conservative gates on both shapes: every parameter a small (≤ 4-byte) `BYVAL`
non-float scalar, numeric-only locals (no string/FLEX cleanup pending), no
`ON ERROR`, no `GOSUB`, a stack callee-cleans convention (not `CDECL`, not a
register convention), and no capturing-lambda environment. The general shape
additionally requires B to be a defined in-module `SUB` — a known local jump
target — and is **not** applied to `FUNCTION`s: a function's result-load
epilogue, and a discarded result's `StrFree`/FPU pop, must still run, so those
fall back to an ordinary `CALL`.
