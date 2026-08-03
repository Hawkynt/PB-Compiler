# O0088 — Boolean materialization via `SBB` / `SETcc`

| | |
|---|---|
| **Status** | ⬜ Planned for the 8086 `SBB` idiom (`SETcc` is already used under [C0001](C0001-386-codegen.md)) |
| **Stage** | Emitter |
| **Related** | [O0031](O0031-branch-fusion.md), [O0032](O0032-short-circuit-conditions.md), [C0001](C0001-386-codegen.md) |

## The idea

[O0031](O0031-branch-fusion.md) removes the −1/0 truth value when nothing reads
it. When the value genuinely **is** needed — assigned to a variable, passed as
an argument, combined bitwise — it should still be produced without a branch.

`SBB AX,AX` turns CF into a full-width mask (`0` or `-1`), which is exactly PB's
truth value, in two bytes and no jump. For the conditions that do not reduce to
CF, the comparison operands or the condition are inverted first.

## Applies to

```basic
DIM x%, f%
f% = (x% < 10)              ' the -1/0 value is stored, so it must exist
```

## Today

```asm
    mov     ax, [x]
    cmp     ax, 000Ah
    jge     False
    mov     ax, 0FFFFh
    jmp     Have
False:
    mov     ax, 0000h
Have:
    mov     [f], ax
```

Five instructions, two of them jumps — and on an 8086 a taken jump flushes the
4-byte prefetch queue.

## Planned

```asm
    mov     ax, [x]
    cmp     ax, 000Ah        ; CF = 1 exactly when x < 10 (unsigned form)
    sbb     ax, ax           ; AX = -1 or 0
    mov     [f], ax
```

## What it needs

- A mapping from each relational operator to a CF-producing comparison, with
  operand swaps and inversions where the signed conditions do not reduce to CF
  directly (a signed `<` needs SF ≠ OF, so it wants `SETcc` on a 386+, or an
  explicit sequence on the 8086).
- Agreement with PB's exact truth representation (−1, not 1) in every path.
