# O0010 — Redundant-statement elimination / `DEF SEG` coalescing

| | |
|---|---|
| **Status** | 🟡 Partial — adjacent `DEF SEG`s coalesce; a window containing a runtime call does not |
| **Stage** | IR memory optimization (`DeadStoreElim`) |
| **Source** | `Ir/IrLowering.cs` (`DEF SEG` is a store to the runtime's `rt_defseg` cell); `Ir/Passes/DeadStoreElim.cs` |
| **Gate** | `--optimize` |
| **Related** | [O0002](O0002-dead-code-elimination.md), [O0016](O0016-value-fact-analysis.md), [O0020](O0020-idiom-replacement.md) |
| **Split into** | [O0211](O0211-console-setter-elimination.md) |

## What it is

A statement whose effect nothing can observe is not emitted.

**This page covers `DEF SEG` coalescing.** `DEF SEG` sets the segment base that
`PEEK`/`POKE`/`BLOAD`/`BSAVE`/`CALL ABSOLUTE` use: when the window from one
`DEF SEG` to the next contains only statements that are provably
**segment-transparent**, the first one changes nothing anyone reads, and it
drops.

Redundant console-state setters collapse by the same argument and are the
separate entry [O0211](O0211-console-setter-elimination.md).

On the IR, `DEF SEG = n` is a store to the runtime's `rt_defseg` cell, so the
coalescing is dead-store elimination: a `DEF SEG` overwritten by the next one
with nothing between them is removed. A runtime call in the window ends it -
`DeadStoreElim` assumes a call may read any memory it can see, and no effect
summary yet says which runtime routines leave `rt_defseg` alone - so the sample
below keeps both stores. (The syntax-level pass that also saw through `PRINT`
and `LOCATE` was retired with the direct emitter.)

## Sample

```basic
DEF SEG = &HB800          ' (1) nothing between here and (2) touches the segment
PRINT "hello"
LOCATE 2, 1
DEF SEG = &HA000          ' (2)
POKE 0, 15
```

## Without the optimizer

```asm
    mov     ax, 0B800h
    mov     [rt_defseg], ax    ; (1) stored, never read
    ...                        ; PRINT / LOCATE
    mov     ax, 0A000h
    mov     [rt_defseg], ax    ; (2)
    ...                        ; POKE
```

## With the optimizer

```asm
    ...                        ; PRINT / LOCATE
    mov     ax, 0A000h
    mov     [rt_defseg], ax
    ...                        ; POKE
```

## Equivalent BASIC

```basic
PRINT "hello"
LOCATE 2, 1
DEF SEG = &HA000
POKE 0, 15
```

## Why it is safe

The window scan is conservative on purpose. Any of the following **ends** it and
keeps the `DEF SEG`:

- a `PEEK`-family expression or a `POKE`/`BLOAD`/`BSAVE`/interrupt statement;
- inline assembly (it may read `rt_defseg` directly);
- a user procedure call (the callee may `PEEK`);
- any control flow — a branch could arrive with a different segment in effect.

## Limits

Two further shapes from the original design are elsewhere or still open:

- **empty loops** collapsing to their counter end value is implemented as an
  idiom, [O0020](O0020-idiom-replacement.md);
- **bounds-check deduplication and hoisting** (as opposed to *removal*, which
  [O0016](O0016-value-fact-analysis.md) does when the check provably cannot
  fire) is roadmap: a check that *can* fail is observable behavior (Error 9), so
  it may only be deduplicated or hoisted, never dropped.
