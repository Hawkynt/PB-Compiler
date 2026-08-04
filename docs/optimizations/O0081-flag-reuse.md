# O0081 — Flag reuse and `TEST` instead of `CMP …,0`

| | |
|---|---|
| **Status** | 🟡 Partial — `CMP reg,0 → TEST reg,reg` ships (`Assembler.Peephole.cs`), and `IF x AND mask` emits `TEST ax, mask` in codegen rather than `AND` + a separate test; the general "reuse the ZF a preceding `ADD/SUB/OR/XOR/INC/DEC/NEG` already set" peephole does not |
| **Stage** | Assembler peephole + codegen |
| **Related** | [O0008](O0008-peephole-zero-idiom.md), [O0031](O0031-branch-fusion.md), [O0038](O0038-instruction-scheduling.md) |

## The idea

Two related rewrites:

1. `CMP r,0` → `TEST r,r` (or the existing `OR r,r`) — two bytes instead of
   three or four, same ZF/SF.
2. **Redundant compare elimination**: `ADD`, `SUB`, `AND`, `OR`, `XOR`, `INC`,
   `DEC` and `NEG` already set ZF/SF from their result. A `CMP result,0` that
   follows one of them with nothing in between that writes flags is pure
   redundancy.

## Applies to

```basic
DIM n%
n% = n% - 1
IF n% = 0 THEN PRINT "done"
```

## Today

```asm
    mov     ax, [n]
    dec     ax
    mov     [n], ax
    mov     ax, [n]
    or      ax, ax           ; the flags DEC already set
    jnz     Skip
```

## Now — bit-test conditions

`IF x AND mask THEN …` is a bit test whose truth is only the AND's zero-ness, so the
code generator emits it as one `TEST` rather than materializing the masked value and
testing it separately:

```asm
    mov     ax, [x]
    test    ax, mask         ; ZF = (x AND mask) == 0 - no `and ax,mask` + `test ax,ax`
    jz      Else
```

Runtime-identical to `and ax,mask; test ax,ax` (the branch reads the same ZF; the AND
result is never stored), and it leaves `AX` holding `x` unmodified. Applies to an
int16 `AND` whose other operand folds to a constant and whose value is not also wanted
for CSE; recognized in `EmitConditionalBranch` before the comparison-fusion path, so it
survives into the `$OPTIMIZE SPEED` scheduler's stream (unlike the peephole below).
Verified by a DOSBox self-diff over several masks and an `absent and-ax-imm8` assertion.

## Planned

```asm
    mov     ax, [n]
    dec     ax
    mov     [n], ax          ; MOV does not touch flags
    jnz     Skip
```

## What it needs

- The assembler's existing **def/use records** (`Asm/Assembler.Schedule.cs`)
  already track `readsFlags`/`writesFlags` per instruction; the pass is a scan
  for a flag-setting instruction whose flags survive to the compare.
- The same narrowness rules as [O0034](O0034-redundant-load-elimination.md): an
  unbroken chain of recorded, byte-adjacent instructions and **no bound label**
  in between, because something could branch in with different flags.
- `INC`/`DEC` do **not** write CF, so a following unsigned condition (`JB`/`JA`)
  may not reuse them — only the ZF/SF conditions qualify.
