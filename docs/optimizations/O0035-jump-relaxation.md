# O0035 — Jump relaxation and threading

| | |
|---|---|
| **Status** | ✅ Implemented (short-form relaxation on every optimized image; jump-to-next removal) |
| **Stage** | Assembler |
| **Source** | `Asm/Assembler.cs` (branch fixups), `Asm/Assembler.Peephole.cs` |
| **Gate** | `--optimize` (was `$OPTIMIZE SIZE` only) |
| **Related** | [O0008](O0008-peephole-zero-idiom.md), [O0041](O0041-branch-layout.md), [P0006](P0006-header-squeeze.md) |
| **Split into** | [O0230](O0230-jump-to-next-removal.md) |

## What it is

A forward branch is emitted in the near form only because its target was still
unbound when the instruction was written. Once the target is known and the
displacement fits a signed byte, the branch is rewritten to the 2-byte **short**
form — smaller and easier on the 8086's 4-byte prefetch queue.

This matters for more than size: the near conditional form `0F 8x rel16` is an
**80386** encoding. On the 8086 this compiler targets, that `0F` byte is
`POP CS`.

Removing a jump whose target is the next instruction is the separate entry
[O0230](O0230-jump-to-next-removal.md).

## Sample

```basic
DIM x%
IF x% = 0 THEN
  PRINT "zero"
END IF
PRINT "done"
```

## Without the optimizer

```asm
    mov     ax, [x]
    or      ax, ax
    db      0Fh, 85h         ; jne near EndIf  (386 encoding!)
    dw      offset EndIf
    ...                      ; PRINT "zero"
    jmp     EndIf            ; to the very next instruction
EndIf:
    ...                      ; PRINT "done"
```

## With the optimizer

```asm
    mov     ax, [x]
    or      ax, ax
    jne     EndIf            ; 75 xx — two bytes, 8086-legal
    ...                      ; PRINT "zero"
EndIf:
    ...                      ; PRINT "done"
```

## Equivalent BASIC

Unchanged — pure encoding.

## Why it is safe

Relaxation only rewrites a branch whose resolved displacement provably fits the
short form, and the removal only applies to a jump whose target is the
immediately following instruction, so control flow is identical in both cases.
Everything downstream is re-laid-out consistently because the assembler owns
every label and fixup.
