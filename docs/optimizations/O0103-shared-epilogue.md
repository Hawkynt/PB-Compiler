# O0103 — Shared epilogue for multiple exits

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter |
| **Related** | [O0095](O0095-branch-tail-merging.md), [O0102](O0102-return-value-forwarding.md), [O0070](O0070-leaf-frame-elision.md) |

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

## What it needs

- Exit-block **placement**, so that the exit which can fall through does; this
  is the same layout question as [O0104](O0104-block-placement.md).
- Interaction with string/FLEX cleanup: an epilogue that frees local handles is
  large enough that sharing is clearly right; a bare `RET` is small enough that
  duplication can be cheaper than a jump on a fetch-bound target.
