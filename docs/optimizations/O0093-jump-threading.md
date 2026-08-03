# O0093 — Jump threading

| | |
|---|---|
| **Status** | ⬜ Planned (the jump-to-next case is done — [O0035](O0035-jump-relaxation.md)) |
| **Stage** | Assembler |
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

## What it needs

- Label reference counts in the assembler (it owns every label and fixup
  already), so an orphaned intermediate block can be deleted rather than merely
  bypassed.
- Care with the short-form relaxation ([O0035](O0035-jump-relaxation.md)): a
  threaded target may be farther away, so relaxation must run **after**
  threading, not before.
- On an 8086 the win is real but modest per site (a taken jump flushes the
  prefetch queue); the size saving is unconditional.
