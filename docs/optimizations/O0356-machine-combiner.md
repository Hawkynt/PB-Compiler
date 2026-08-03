# O0356 — Machine combiner

| | |
|---|---|
| **Status** | ⬜ Planned |
| **Stage** | After instruction selection |
| **Related** | [O0082](O0082-memory-operand-folding.md), [O0064](O0064-lea-fusion.md), [O0038](O0038-instruction-scheduling.md) |

## The idea

Some target patterns only become visible **after** selection, when the actual
instructions and registers are known: a `MOV` plus an `ADD` that could have been
one `LEA`, an address computation that could fold into the next operand, a
compare that could reuse a flag-setting instruction's result.

A late combiner re-examines the selected stream and recombines those pairs —
which is different from a peephole in that it may consult the cost model and
undo an earlier choice.

## Applies to

```asm
    mov     ax, bx
    add     ax, si
    add     ax, 4            ; three instructions, one LEA
```

## What it needs

- The recorded instruction stream the assembler already keeps for the peephole
  and the scheduler ([O0038](O0038-instruction-scheduling.md)).
- A cost model to decide when the combination is actually better
  ([O0174](O0174-target-cost-models.md)) — `LEA` is free of flag effects but is
  not always faster on an 8086.
- Care with **flag liveness**: `LEA` does not set flags, so combining an `ADD`
  into it is only legal when nothing reads them.
