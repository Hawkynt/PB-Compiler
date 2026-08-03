# O0094 — Conditional branch inversion

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | Emitter / assembler |
| **Related** | [O0041](O0041-branch-layout.md), [O0093](O0093-jump-threading.md), [O0104](O0104-block-placement.md) |

## The idea

```asm
    jcc     else
    ...                      ; then
    jmp     end
else:
    ...                      ; else
end:
```

When the `ELSE` arm is the shorter or the colder one, inverting the condition
and swapping the arms removes the `JMP end` entirely — one fewer taken branch on
the path that runs.

[O0041](O0041-branch-layout.md) already chooses the *shape* so the `THEN` body
falls through; this pass revisits that choice per site, once the arm sizes and
the estimated probabilities are known.

## Applies to

```basic
DIM x%
IF x% = 0 THEN
  PRINT "a lengthy uncommon path"
  PRINT "with several statements"
ELSE
  n% = n% + 1
END IF
```

## Today

The `THEN` arm falls through and the short `ELSE` arm is reached by a branch,
with a `JMP` closing the `THEN` arm.

## Planned

```asm
    jne     Else             ; inverted
    inc     word ptr [n]     ; the short arm falls through
    jmp     End
Else:
    ...                      ; the long arm
End:
```

## What it needs

- Arm **size estimates** at emission time, or a post-pass on the assembled
  regions (the assembler tracks region boundaries already for
  [O0040](O0040-identical-code-folding.md)).
- Branch probability where available ([O0104](O0104-block-placement.md)) — an
  inversion that puts the *likely* arm behind a taken branch is a loss on every
  target with static prediction.
