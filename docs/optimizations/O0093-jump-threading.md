# O0093 — Jump threading

| | |
|---|---|
| **Status** | ✅ Done — threading plus removal of the orphaned intermediate jump |
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

## Removing the orphan

`RemoveOrphanedJumpHops` (same file, run at the end of `RunJumpThreading`) deletes
the `A: JMP B` hop once threading has bypassed it, reclaiming the bytes the
taken-jump saving left behind. Two conditions gate it, both conservative, because
a wrong deletion here is a miscompile anywhere:

- **Nothing may target it.** Every fixup's resolved destination is collected as
  `Position + Addend` — not just `Position`, so a label reached through an addend
  still counts. A *named* label on the hop keeps it as well: another module may
  import that name and this assembler cannot see it.
- **Control may not fall into it.** A hop reached by falling off the end of the
  preceding instruction is live however few things jump to it. The only
  instruction proven not to fall through is another unconditional `JMP` ending
  exactly at the hop's first byte. `RET` would qualify too but is not attempted —
  `C3` cannot be told from a displacement byte by looking at it.

Deletion only ever *shrinks* the distance a jump spans, so no short displacement
can be pushed out of range by it; the pass runs before relaxation either way.
Cuts go from the end backwards so earlier offsets stay valid, and the whole thing
iterates (bounded) because removing one hop can orphan the next.

Covered by `OrphanedJumpHopTests`, which compares two builds rather than naming
byte counts — these images are small enough that jumps take their short form, and
one hop going away can take the next with it, so an absolute length is a statement
about the layout rather than about the pass.
