# O0014 — Tail-call optimization

| | |
|---|---|
| **Status** | 🟡 Partial — self tail calls are implemented; general cross-procedure tail calls ([O0213](O0213-cross-procedure-tail-call.md)) are not implemented on the IR path |
| **Stage** | IR middle end |
| **Source** | `Ir/Passes/TailRecursion.cs` — `Run`, `TailSelfCall`; registered as `tailrec` in `IrMiddleEndPipeline.Standard()` |
| **Gate** | `--optimize` |
| **Verified by** | `tests/diff/DIFF29.BAS` (self-recursion + blocked twins), `DIFF87.BAS` (mutual recursion, differing argument counts, a deliberately non-tail call), `BackendTailRecursionTests` |
| **Related** | [O0006](O0006-inlining.md), [O0070](O0070-leaf-frame-elision.md) |
| **Split into** | [O0213](O0213-cross-procedure-tail-call.md) |

## What it is

**This page covers the self-call.** A self-call in tail position — nothing
between it and the return, and the return passing on the call's own result —
becomes a loop. `TailRecursion` pushes a new entry block in front of the old
one, which becomes a loop header; each parameter turns into a phi taking the
original argument on the way in and the call's argument on the way round, and
the call plus its return become a branch back to the header. It is not a size
or speed optimization: without it a deep recursion overflows the stack.

Recursion then runs in **constant stack space**: a 60 000-deep tail recursion
completes where the genuine compiler's default 2 KiB stack dies at about 170
frames.

The cross-procedure form is [O0213](O0213-cross-procedure-tail-call.md), and
it is not implemented on the IR path; the syntax-level version was retired with
the direct emitter. Mutual recursion still runs in constant stack when the
inliner first turns `A calls B calls A` into a self-call, which `tailrec` then
rewrites.

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

The pass declines a call that is not in tail position — anything between it
and the return, or a return of some other value, means the frame is still
needed. It declines a whole function if any alloca's address escapes, because
reusing one frame is only equivalent when no level can still hold a pointer
into the one before it. A function with an armed error handler or inline
assembly is never touched.
