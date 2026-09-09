# O0065 — Dead frame-store elimination

| | |
|---|---|
| **Status** | 🟡 Partial (same-block overwritten frame stores are removed; whole-procedure last-store proof still needs complete memory recording/private temp metadata) |
| **Stage** | Assembler |
| **Related** | [O0034](O0034-redundant-load-elimination.md), [O0038](O0038-instruction-scheduling.md), [O0060](O0060-memory-ssa.md) |

## The idea

Once [redundant-load elimination](O0034-redundant-load-elimination.md) has
removed the last reader of a spill or CSE cell, the store *into* it is dead. The
max-scan idiom leaves exactly one such `MOV [BP-8],AX` per iteration — the last
instruction in that loop with nothing left to justify it.

## Applies to

```basic
$OPTIMIZE SPEED
DIM a%(0 TO 99), i%, m%
FOR i% = 0 TO 99
  IF a%(i%) > m% THEN m% = a%(i%)
NEXT
```

## Today

```asm
    mov     ax, [bx]
    mov     [bp-8], ax       ; CSE define — nothing reads it any more
    cmp     ax, di
    jle     Skip
    mov     di, ax
Skip:
```

## Planned

```asm
    mov     ax, [bx]
    cmp     ax, di
    jle     Skip
    mov     di, ax
Skip:
```

## Now

The late assembler pass implements the recording-proven local form of dead-store
elimination and deliberately composes it **after** O0034 load forwarding.

A plain full-word frame store is removed when all of these are true:

- it is `MOV [BP+disp], r16` or `MOV WORD PTR [BP+disp], imm16`;
- a later plain full-word `MOV` reaches the **same** BP-relative cell by uninterrupted
  straight-line fall-through and completely overwrites it;
- no surviving memory read that may alias the cell occurs first;
- no conditional branch occurs between the stores — its taken path could skip the
  replacement, leaving the older value observable;
- no label appears at the first store or between the stores;
- there is no unrecorded gap such as a call or inline-asm instruction;
- a partial write, read-modify-write operation, or unknown alias declines the proof.

The branch rule is intentionally stricter than O0034 forwarding. A forwarded load
*in the fall-through path* of a conditional branch is reached only after the older
store and can safely use its register value. Dead-store elimination asks a different
question: whether the **later overwrite is guaranteed to execute**. A conditional
branch makes that false, so it terminates the DSE scan even when its target lies
beyond the overwrite.

This means the useful straight-line composition works automatically:

```asm
    mov     [bp-8], ax
    mov     dx, [bp-8]       ; O0034 -> mov dx,ax
    mov     [bp-8], cx       ; complete overwrite
```

becomes

```asm
    mov     dx, ax
    mov     [bp-8], cx
```

O0034 first removes the only actual frame read; O0065 then sees that the older
store is unobservable before the replacement store and removes it. Multiple
consecutive stores cascade naturally, keeping only the value that can still be
observed.

The implementation lives in `Assembler.LoadForward.cs`, sharing the same
`SchedInstr` memory identity and `MemMayAlias` rules as forwarding and scheduling.
All byte cuts use the common `RemoveBytes` path, so labels, fixups, relocations and
instruction-record offsets remain synchronized.

## Still blocked

The stronger whole-procedure claim from the original design is **not** made yet.
Proving that a final store is dead merely because no later *recorded* instruction
reads it is unsound: `LEA`, `PUSH mem`, read-modify-write memory forms, indirect
operations and other unrecorded instructions may still observe the cell.

That is why the max-scan example above can still retain its final per-iteration
CSE store when there is no later overwrite. Finishing that form needs either:

- complete memory def/use recording for every instruction shape that can observe a
  frame cell, or
- code-generator metadata declaring which frame range contains compiler-private
  spill/CSE temporaries whose addresses never escape.

Until one of those exists, the pass stops at every conditional branch and every
record gap and never treats "no later recorded read" as proof of death.
