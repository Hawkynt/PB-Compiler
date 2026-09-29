# O0238 — `SETcc` relational results

| | |
|---|---|
| **Status** | ⬜ Not implemented on the IR path |
| **Stage** | x86 back end (instruction selection, planned) |
| **Source** | None. `Backend/InstructionSelector.cs` materializes a comparison's value with `MOV dest,-1` / `Jcc` / `MOV dest,0` on every CPU; `SETcc` is not in the back end's `MOpcode` set (`Backend/MachineIr.cs`) |
| **Gate** | `--optimize` + `$CPU 80386` |
| **Split from** | [C0001](C0001-386-codegen.md) |

## What it is

When a comparison's −1/0 value is genuinely needed, `SETcc` produces it
**branchlessly** on a 386+, instead of the branch-and-load pair.

Not implemented on the IR path; the syntax-level version was retired with the
direct emitter. The instruction selector always uses the branch form.

## Sample

```basic
$CPU 80386
DIM x%, f%
f% = (x% > 10)
```

## Without / with

```asm
    cmp     ax, 000Ah        ; without
    jle     False
    mov     ax, 0FFFFh
    jmp     Have
False:
    mov     ax, 0000h
Have:

    cmp     ax, 000Ah        ; with
    setg    al
    movzx   ax, al
    neg     ax               ; PB's TRUE is -1, not 1
```

## Why it is safe

`SETcc` writes 0 or 1 from the same flags the branch would have tested; the
negation converts to PB's −1/0 convention exactly. No branch means no
misprediction and no prefetch flush.

## See also

- The 8086 equivalent, using the carry flag as a mask:
  [O0088](O0088-boolean-materialization-sbb.md).
- When the value is *not* needed at all: [O0031](O0031-branch-fusion.md).
