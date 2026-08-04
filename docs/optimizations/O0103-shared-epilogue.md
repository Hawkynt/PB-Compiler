# O0103 — Shared epilogue for multiple exits

| | |
|---|---|
| **Status** | 🟡 Partial (the shared epilogue with a no-jump fall-through is produced today; the per-exit duplicate-vs-share cost choice is not) |
| **Stage** | Emitter |
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

The **sharing** half — the doc's "Planned" example — is what the emitter produces
today, and it falls out of the baseline design rather than a dedicated pass. Each
procedure has one `_epilogue` label (`CodeGenerator.Procs.cs`), marked once; every
`EXIT SUB`/`EXIT FUNCTION`/`EXIT DEF` jumps to it (`EmitExit`), and the natural end
of the body falls through into it. When an `EXIT` sits physically last, its
`JMP`-to-next is deleted by [O0230](O0230-jump-to-next-removal.md)
(`RunJumpRelaxation`), so the fall-through path carries no jump at all. Verified:
the doc's three-exit `Process` example emits exactly **one** frame teardown
(`MOV SP,BP` / `POP BP` / `RET 2`), shared by both `EXIT SUB`s and the
fall-through, not three copies.

## Still planned

- The per-exit **duplicate-vs-share** cost choice. The doc's "the right answer is
  both, chosen per exit" — duplicating a small teardown at some exits to save the
  jump on a fetch-bound target, sharing a large (string/FLEX-freeing) one — has no
  code; every exit shares unconditionally. This is coupled to exit-block
  **placement** ([O0104](O0104-block-placement.md)): choosing *which* exit falls
  through is the same layout question.
