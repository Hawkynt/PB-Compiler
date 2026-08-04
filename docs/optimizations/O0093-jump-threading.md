# O0093 — Jump threading

| | |
|---|---|
| **Status** | 🟡 Partial (the threading itself is done and wired under `--optimize`; deleting the now-orphaned intermediate jump is the remaining piece) |
| **Stage** | Assembler |
| **Source** | `Asm/Assembler.cs` (`RunJumpThreading`) |
| **Related** | [O0035](O0035-jump-relaxation.md), [O0094](O0094-branch-inversion.md), [O0107](O0107-branch-folding-through-phi.md) |

## The idea

A branch whose target is itself an unconditional branch should go straight to
the final destination:

```
Jcc A ; A: JMP B   ->   Jcc B
```

The intermediate block disappears once nothing else targets it. Chains resolve
transitively.

## Applies to

```basic
DIM x%
IF x% > 0 THEN
  IF x% < 10 THEN PRINT "small" ELSE PRINT "big"
ELSE
  PRINT "big"
END IF
```

Nested control flow of this shape routinely produces arm-closing jumps that land
on other jumps.

## Today

```asm
    cmp     ax, 000Ah
    jge     L1
    ...
    jmp     L2
L1: jmp     L3               ; a jump to a jump
L2: ...
L3:
```

## Planned

```asm
    cmp     ax, 000Ah
    jge     L3               ; threaded
    ...
    jmp     L2
L2: ...
L3:
```

## Now

`RunJumpThreading` (in `Asm/Assembler.cs`) rewrites every real jump — short/near
`JMP` (`EB`/`E9`) and near `Jcc` (`0F 8x`) — whose destination is itself an
unconditional `JMP` to point straight at the final target, following chains with
an 8-hop budget so an intentional jump cycle (an endless loop) terminates. `CALL`
keeps its target; a short jump only retargets while the byte displacement still
reaches. It is a pure fixup rewrite (byte-length-preserving) run after the
peephole/scheduler and **before** relaxation, so a threaded-farther target is
still handled correctly by the short-form pass. Wired on for every standalone
module under `--optimize` (`EnableJumpThreading = standalone`); it collapses the
`ITERATE → loop-end → loop-head` and `GOTO → GOTO` cascades that nested control
flow routinely emits. Covered by `JumpThreadingTests` (jmp-to-jmp, `Jcc`,
transitive chains, and the CALL-is-never-threaded guard).

## Still planned

- **Deleting the orphaned intermediate block.** Threading only *bypasses* the
  `A: JMP B` hop; when nothing else targets `A` any more it is now dead code,
  but the assembler does not yet reference-count labels to prove that and remove
  the bytes. The taken-jump saving (the actual runtime win, and the harder-on-the
  8086 prefetch flush) is already realized; this is the remaining size saving.
