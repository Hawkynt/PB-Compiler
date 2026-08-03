# O0169 — Returned-condition propagation

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Whole-program + emitter |
| **Related** | [O0031](O0031-branch-fusion.md), [O0088](O0088-boolean-materialization-sbb.md), [O0021](O0021-register-parameters.md) |

## The idea

A `FUNCTION` that returns a truth value materializes PB's −1/0, returns it, and
the caller immediately tests it — the cross-procedure version of exactly what
[O0031](O0031-branch-fusion.md) removes within one procedure.

With an internal calling convention the compiler owns
([O0021](O0021-register-parameters.md)), such a function can leave its answer
**in the flags**, and the caller branches on them directly.

## Applies to

```basic
FUNCTION IsReady%(BYVAL s%)
  IsReady% = (s% AND 4) <> 0
END FUNCTION

IF IsReady%(state%) THEN PRINT "go"
```

## Today

```asm
IsReady:
    mov     ax, [bp+6]
    and     ax, 0004h
    jz      False
    mov     ax, 0FFFFh
    jmp     Done
False:
    mov     ax, 0000h
Done:
    ret     2
    ...
    call    IsReady
    or      ax, ax           ; and immediately test what was just built
    jz      Skip
```

## Planned

```asm
IsReady:
    mov     ax, [bp+6]
    test    ax, 0004h        ; the flags ARE the answer
    ret     2
    ...
    call    IsReady
    jz      Skip
```

## What it needs

- An **internal convention** that designates the flags as a result location, and
  the ownership proof that no external caller can see it
  ([O0021](O0021-register-parameters.md)).
- The `RET` must not disturb the flags — it does not — and neither may any
  epilogue code, which rules out frames with string cleanup.
- A fallback materialization ([O0088](O0088-boolean-materialization-sbb.md)) for
  callers that need the value rather than the branch.
