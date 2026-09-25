# O0103 — Shared epilogue for multiple exits

| | |
|---|---|
| **Status** | ⬜ Not implemented on the IR path (every exit gets its own copy of the epilogue; there is no sharing and no per-exit cost choice) |
| **Stage** | x86 back end (emission) |
| **Source** | None. `Ir/IrLowering.cs` — `ReturnFromFunction` ends each `EXIT` with its own `ret`; `Backend/MachineEmitter.cs` expands every `Ret` in place (`EmitEpilogue`, or `EmitReturn` when the frame is elided) |
| **Related** | [O0095](O0095-branch-tail-merging.md), [O0102](O0102-return-value-forwarding.md), [O0070](O0070-leaf-frame-elision.md), [O0230](O0230-jump-to-next-removal.md) |

## The idea

A procedure with several `EXIT SUB`/`EXIT FUNCTION` points either duplicates the
epilogue at each one, or routes them all through one epilogue with a jump. The
right answer is *both*, chosen per exit: a block that already falls into the
epilogue needs no jump, while a distant one should share rather than duplicate.

## Applies to

```basic
SUB Process(BYVAL k%)
  IF k% < 0 THEN EXIT SUB
  IF k% = 0 THEN EXIT SUB
  PRINT k%
END SUB
```

## Today

Either three copies of the frame teardown, or three jumps to one — including a
jump from the block physically adjacent to it.

## Planned

```asm
    cmp     ax, 0000h
    jl      Epilogue         ; shared
    je      Epilogue
    ...                      ; PRINT k%
Epilogue:                    ; the fall-through path needs no jump at all
    mov     sp, bp
    pop     bp
    ret     2
```

## Now

Not implemented on the IR path; the syntax-level version was retired with the
direct emitter. The lowering ends every `EXIT SUB`/`EXIT FUNCTION`/`EXIT DEF`
with its own `ret` (after any owned-string release), and `MachineEmitter` expands
each `Ret` into a full teardown (`MOV SP,BP` / `POP BP` / `RET n`) where it
stands, so the three-exit `Process` example carries three copies. When the frame
is elided ([O0070](O0070-leaf-frame-elision.md)) each copy is just `RET n`.

The retired emitter used one `_epilogue` label per procedure: every `EXIT` jumped
to it, the body fell through into it, and a `JMP`-to-next was deleted by
[O0230](O0230-jump-to-next-removal.md).

## Still planned

- **Sharing** the epilogue between exits (the "Planned" listing above).
- The per-exit **duplicate-vs-share** cost choice. The doc's "the right answer is
  both, chosen per exit" — duplicating a small teardown at some exits to save the
  jump on a fetch-bound target, sharing a large (string/FLEX-freeing) one — has no
  code; every exit duplicates unconditionally. This is coupled to exit-block
  **placement** ([O0104](O0104-block-placement.md)): choosing *which* exit falls
  through is the same layout question.
